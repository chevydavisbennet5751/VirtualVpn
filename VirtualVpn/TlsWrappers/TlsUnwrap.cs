﻿using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using VirtualVpn.Helpers;
using VirtualVpn.TcpProtocol;

namespace VirtualVpn.TlsWrappers;

/// <summary>
/// Wrap a socket connected to a TLS session,
/// unpack it with a set a certificates (this
/// class acting as the 'server').
/// Then expose the underlying plain stream
/// <p></p>
/// This class is the server version of <see cref="TlsHttpProxyCallAdaptor"/>.
/// This class is agnostic of the underlying protocol.
/// </summary>
public class TlsUnwrap : ISocketAdaptor
{
    private readonly SslServerAuthenticationOptions _authOptions;
    private readonly X509Certificate _certificate;
    private readonly ISocketAdaptor _socket;
    
    private readonly BlockingBidirectionalBuffer _buffer;

    /// <summary>
    /// Try to create a TLS re-wrapper, given paths to certificates and a connection.
    /// If the certificates can't be loaded, the connection function will not be called,
    /// and an exception will be thrown.
    /// <p></p>
    /// Due to unresolved bugs in that operating system, this will probably not work on Windows.
    /// </summary>
    /// <param name="tlsKeyPaths">Paths to PEM keys, private first then public. separated by ';'. e.g. "/var/certs/privkey.pem;/var/certs/fullchain.pem"</param>
    /// <param name="outgoingConnectionFunction">Function that will start the OUTGOING socket, NOT the incoming client call.</param>
    public TlsUnwrap(string tlsKeyPaths, Func<ISocketAdaptor> outgoingConnectionFunction)
    {
        if (string.IsNullOrWhiteSpace(tlsKeyPaths)) throw new Exception("Must have valid paths to PEM files to start TLS re-wrap");
        
        var filePaths = tlsKeyPaths.Split(';');
        if (filePaths.Length != 2) throw new Exception("TLS key paths must have exactly two files specified, separated by ';'");
        
        var privatePath = filePaths[0];
        var publicPath = filePaths[1];
        
        if (!File.Exists(privatePath)) throw new Exception($"Private key is not present, or this service does not have permissions to access it ({privatePath})");
        if (!File.Exists(publicPath)) throw new Exception($"Private key is not present, or this service does not have permissions to access it ({publicPath})");
        
        var enabledProtocols = (Platform.Current() == Platform.Kind.Windows)
                ? SslProtocols.Tls11 | SslProtocols.Tls12 // DO NOT use 1.3 on Windows: https://github.com/dotnet/runtime/issues/1720
                : SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Tls13;
        
        _authOptions = new SslServerAuthenticationOptions{
            AllowRenegotiation = true,
            ClientCertificateRequired = false,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            ServerCertificateSelectionCallback = CertSelect,
            EnabledSslProtocols = enabledProtocols,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        };
        
        _certificate = GetX509Certificate(privatePath, publicPath);
        
        _socket = outgoingConnectionFunction();
        _buffer = new BlockingBidirectionalBuffer();
        
        
        // TODO: do this stuff
        //     - hook up double-direction buffered stream
        //     - hook up buffer to 'Available'
        //     - make 'auth as server' call
    }

    /// <summary>Release the underlying socket</summary>
    public void Dispose()
    {
        _socket.Dispose();
    }

    /// <summary>
    /// Close the underlying connection
    /// </summary>
    public void Close()
    {
        _socket.Close();
    }

    /// <summary>
    /// True if the underlying socket is in a connected state
    /// </summary>
    public bool Connected => _socket.Connected;
    
    /// <summary>
    /// Number of bytes available to be read from <see cref="OutgoingFromLocal"/>
    /// </summary>
    public int Available { get; }
    
    
    /// <summary>
    /// Called externally. This should be data coming from the remote
    /// client. This is our 'hello' source, and will be encrypted.
    /// <p></p>
    /// Buffer is arbitrarily sized, and we return what we could read.
    /// </summary>
    public int IncomingFromTunnel(byte[] buffer, int offset, int length)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Called externally. This is where we supply the decoded data,
    /// ready to be moved to the other side by <see cref="TcpAdaptor"/>.
    /// <p></p>
    /// Buffer is expected to be at-most the size of <see cref="Available"/>
    /// and we should try to entirely fill it, returning the number of bytes
    /// copied.
    /// <p></p>
    /// Data is supplied unencrypted, it is up to the TcpAdaptor to
    /// establish an encrypted tunnel where appropriate.
    /// </summary>
    public int OutgoingFromLocal(byte[] buffer)
    {
        throw new NotImplementedException();
    }
    
    #region Certificates

    private X509Certificate CertSelect(object sender, string? hostname)
    {
        Console.WriteLine($"Returning cert for {hostname} with {_certificate.Subject}");

        if (Platform.Current() == Platform.Kind.Windows)
        {
            // There are a bunch of bugs, and no-one seems to want to fix them.
            // See
            //  - https://github.com/dotnet/runtime/issues/23749
            //  - https://github.com/dotnet/runtime/issues/45680
            //  - https://github.com/dotnet/runtime/issues/23749
            //  - https://github.com/dotnet/runtime/issues/27826
            // These bugs were closed at time of writing, but not actually fixed.
            
            if (hostname is null || !_certificate.Subject.Contains(hostname))
                throw new Exception("Windows does not support providing certificates without matching 'CN'. " +
                                    "If you are testing, consider putting the DNS name in C:\\Windows\\System32\\drivers\\etc\\hosts file");
        }

        return _certificate;
    }
    

    private static X509Certificate GetX509Certificate(string privateKeyFile, string certificateFile)
    {
        var certPem = File.ReadAllText(certificateFile);
        var keyPem = File.ReadAllText(privateKeyFile);
        var certFromPem = X509Certificate2.CreateFromPem(certPem, keyPem);

        if (Platform.Current() != Platform.Kind.Windows) return certFromPem;
        
        return ReWrap(certFromPem);
    }

    /// <summary>
    /// Works around some of the many bugs in Windows cert store
    /// </summary>
    private static X509Certificate2 ReWrap(X509Certificate2 certFromPem)
    {
        return new X509Certificate2(certFromPem.Export(X509ContentType.Pkcs12));
    }

    #endregion
}