﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Bedrock.Framework.Infrastructure;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;

namespace Bedrock.Framework.Middleware.Tls;

internal class TlsServerConnectionMiddleware
{
    private readonly ConnectionDelegate _next;
    private readonly TlsOptions _options;
    private readonly ILogger _logger;
    private readonly X509Certificate2 _certificate;
    private readonly Func<ConnectionContext, string, X509Certificate2> _certificateSelector;

    public TlsServerConnectionMiddleware(ConnectionDelegate next, TlsOptions options, ILoggerFactory loggerFactory)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _next = next;

        // capture the certificate now so it can't be switched after validation
        _certificate = options.LocalCertificate;
        _certificateSelector = options.LocalServerCertificateSelector;
        if (_certificate == null && _certificateSelector == null)
        {
            throw new ArgumentException("Server certificate is required", nameof(options));
        }

        // If a selector is provided then ignore the cert, it may be a default cert.
        if (_certificateSelector != null)
        {
            // SslStream doesn't allow both.
            _certificate = null;
        }
        else
        {
            EnsureCertificateIsAllowedForServerAuth(_certificate);
        }

        _options = options;
        _logger = loggerFactory?.CreateLogger<TlsServerConnectionMiddleware>();
    }

    public Task OnConnectionAsync(ConnectionContext context)
    {
        return Task.Run(() => InnerOnConnectionAsync(context));
    }

    private async Task InnerOnConnectionAsync(ConnectionContext context)
    {
        bool certificateRequired;
        var feature = new TlsConnectionFeature();
        context.Features.Set<ITlsConnectionFeature>(feature);
        context.Features.Set<ITlsHandshakeFeature>(feature);

        var memoryPool = context.Features.Get<IMemoryPoolFeature>()?.MemoryPool;

        var inputPipeOptions = new StreamPipeReaderOptions
        (
            pool: memoryPool,
            bufferSize: memoryPool.GetMinimumSegmentSize(),
            minimumReadSize: memoryPool.GetMinimumAllocSize(),
            leaveOpen: true
        );

        var outputPipeOptions = new StreamPipeWriterOptions
        (
            pool: memoryPool,
            leaveOpen: true
        );

        SslDuplexPipe sslDuplexPipe = null;

        if (_options.RemoteCertificateMode == RemoteCertificateMode.NoCertificate)
        {
            sslDuplexPipe = new SslDuplexPipe(context.Transport, inputPipeOptions, outputPipeOptions);
            certificateRequired = false;
        }
        else
        {
            sslDuplexPipe = new SslDuplexPipe(context.Transport, inputPipeOptions, outputPipeOptions, s => new SslStream(
                s,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (sender, certificate, chain, sslPolicyErrors) =>
                {
                    if (certificate == null)
                    {
                        return _options.RemoteCertificateMode != RemoteCertificateMode.RequireCertificate;
                    }

                    if (_options.RemoteCertificateValidation == null)
                    {
                        if (sslPolicyErrors != SslPolicyErrors.None)
                        {
                            return false;
                        }
                    }

                    var certificate2 = ConvertToX509Certificate2(certificate);
                    if (certificate2 == null)
                    {
                        return false;
                    }

                    if (_options.RemoteCertificateValidation != null)
                    {
                        if (!_options.RemoteCertificateValidation(certificate2, chain, sslPolicyErrors))
                        {
                            return false;
                        }
                    }

                    return true;
                }));

            certificateRequired = true;
        }

        var sslStream = sslDuplexPipe.Stream;

        using (var cancellationTokeSource = new CancellationTokenSource(Debugger.IsAttached ? Timeout.InfiniteTimeSpan : _options.HandshakeTimeout))
        {
            try
            {
                // Adapt to the SslStream signature
                ServerCertificateSelectionCallback selector = null;
                if (_certificateSelector != null)
                {
                    selector = (sender, name) =>
                    {
                        context.Features.Set(sslStream);
                        var cert = _certificateSelector(context, name);
                        if (cert != null)
                        {
                            EnsureCertificateIsAllowedForServerAuth(cert);
                        }
                        return cert;
                    };
                }

                var sslOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ServerCertificateSelectionCallback = selector,
                    ClientCertificateRequired = certificateRequired,
                    EnabledSslProtocols = _options.SslProtocols,
                    CertificateRevocationCheckMode = _options.CheckCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                    ApplicationProtocols = new List<SslApplicationProtocol>(),
                    CipherSuitesPolicy = _options.CipherSuitesPolicy
                };

                _options.OnAuthenticateAsServer?.Invoke(context, sslOptions);

                await sslStream.AuthenticateAsServerAsync(sslOptions, cancellationTokeSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug(2, "Authentication timed out");
                await sslStream.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is AuthenticationException)
            {
                _logger?.LogDebug(1, ex, "Authentication failed");
                await sslStream.DisposeAsync().ConfigureAwait(false);
                return;
            }
        }

        feature.ApplicationProtocol = sslStream.NegotiatedApplicationProtocol.Protocol;
        context.Features.Set<ITlsApplicationProtocolFeature>(feature);
        feature.LocalCertificate = ConvertToX509Certificate2(sslStream.LocalCertificate);
        feature.RemoteCertificate = ConvertToX509Certificate2(sslStream.RemoteCertificate);
        feature.CipherAlgorithm = sslStream.CipherAlgorithm;
        feature.CipherStrength = sslStream.CipherStrength;
        feature.HashAlgorithm = sslStream.HashAlgorithm;
        feature.HashStrength = sslStream.HashStrength;
        feature.KeyExchangeAlgorithm = sslStream.KeyExchangeAlgorithm;
        feature.KeyExchangeStrength = sslStream.KeyExchangeStrength;
        feature.Protocol = sslStream.SslProtocol;

        var originalTransport = context.Transport;

        try
        {
            context.Transport = sslDuplexPipe;

            // Disposing the stream will dispose the sslDuplexPipe
            await using (sslStream)
            await using (sslDuplexPipe)
            {
                await _next(context).ConfigureAwait(false);
                // Dispose the inner stream (SslDuplexPipe) before disposing the SslStream
                // as the duplex pipe can hit an ODE as it still may be writing.
            }
        }
        finally
        {
            // Restore the original so that it gets closed appropriately
            context.Transport = originalTransport;
        }
    }

    protected static void EnsureCertificateIsAllowedForServerAuth(X509Certificate2 certificate)
    {
        if (!CertificateLoader.IsCertificateAllowedForServerAuth(certificate))
        {
            throw new InvalidOperationException($"Invalid server certificate for server authentication: {certificate.Thumbprint}");
        }
    }

    private static X509Certificate2 ConvertToX509Certificate2(X509Certificate certificate)
    {
        if (certificate is null)
        {
            return null;
        }

        return certificate as X509Certificate2 ?? new X509Certificate2(certificate);
    }
}
