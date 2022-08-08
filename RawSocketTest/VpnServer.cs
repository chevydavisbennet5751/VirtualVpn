﻿using System.Net;
using SkinnyJson;

namespace RawSocketTest;

public class VpnServer : IDisposable
{
    private const int IkeHeader = 0;
    
    private int _messageCount;
    private readonly UdpServer _server;
    private readonly Dictionary<ulong, VpnSession> _sessions = new();

    public VpnServer()
    {
        _server = new UdpServer(IkeResponder, SpeResponder);
    }

    public void Run()
    {
        _messageCount = 0;
        Console.WriteLine("Setup");
        Json.DefaultParameters.EnableAnonymousTypes = true;

// Use Gerty for testing. Run "JustListen" to check your connection is working.
// You may need to do lots of firewall poking and NAT rules.
// Switch on ipsec on Gerty (`ipsec restart`), make sure there is a ruleset for the test PC.

//var target = new IPEndPoint(new IPAddress(new byte[]{197,250,65,132}), 500); // M-P
//var target = new IPEndPoint(new IPAddress(new byte[] { 159, 69, 13, 126 }), 500); // Gerty

        Thread.Sleep(1000);

        _server.Start();

        var limit = 50;
        for (int i = 0; i < limit; i++)
        {
            // wait for other side to contact:
            Console.Write(".");
            Thread.Sleep(1500);
        }
    }



    /// <summary>
    /// Responds to port 500 traffic
    /// </summary>
    private void IkeResponder(byte[] rawData, IPEndPoint sender)
    {
        // write capture to file for easy testing
        _messageCount++;
        var name = @$"C:\temp\IKEv2-{_messageCount}_Port-{sender.Port}_IKE.bin";
        File.WriteAllBytes(name, rawData);
        Console.WriteLine($"Got a 500 packet -- {name}");
        
        IkeSessionResponder(rawData, sender, sendZeroHeader: false);
    }

    private void IkeSessionResponder(byte[] data, IPEndPoint sender, bool sendZeroHeader)
    {
        // read the message to figure out session data
        var ikeMessage = IkeMessage.FromBytes(data, 0);

        Console.WriteLine($"Got a 500 packet ex={ikeMessage.Exchange}");

        if (ikeMessage.Exchange == ExchangeType.IDENTITY_1) // start of an IkeV1 session
        {
            Console.WriteLine("    it's for IKEv1. Not supported, not replying");
            return;
        }

        if (_sessions.ContainsKey(ikeMessage.SpiI))
        {
            Console.WriteLine($"    it's for an existing session started elsewhere? {ikeMessage.SpiI:x16} => {ikeMessage.SpiR:x16}");
            
            // Pass message to existing session
            _sessions[ikeMessage.SpiI].HandleIke(ikeMessage, sender, sendZeroHeader);
            return;
        }

        if (_sessions.ContainsKey(ikeMessage.SpiR))
        {
            Console.WriteLine($"    it's for an existing session we started? {ikeMessage.SpiI:x16} => {ikeMessage.SpiR:x16}");
            
            // Pass message to existing session
            _sessions[ikeMessage.SpiR].HandleIke(ikeMessage, sender, sendZeroHeader);
            return;
        }

        Console.WriteLine($"    it's for a new session?  {ikeMessage.SpiI:x16} => {ikeMessage.SpiR:x16}");
            
        // Start a new session and store it, keyed by the initiator id
        var newSession = new VpnSession(_server, ikeMessage.SpiI);
        _sessions.Add(ikeMessage.SpiI, newSession);
            
        // Pass message to new session
        newSession.HandleIke(ikeMessage, sender, sendZeroHeader); 
    }

    /// <summary>
    /// Responds to port 4500 traffic
    /// </summary>
    private void SpeResponder(byte[] data, IPEndPoint sender)
    {
        // write capture to file for easy testing
        _messageCount++;
        var name = @$"C:\temp\IKEv2-{_messageCount}_Port-{sender.Port}_SPE.bin";
        File.WriteAllBytes(name, data);
        Console.WriteLine($"Got a 4500 packet -- {name}");
        
        // Check for keep-alive ping?
        if (data.Length < 4 && data[0] == 0xff)
        {
            Console.WriteLine("    Looks like a keep-alive ping. Sending pong");
            _server.SendRaw(data, sender, out _);
            return;
        }
        
        // Check for "IKE header" (prefix of 4 zero bytes)
        var idx = 0;
        var header = Bit.ReadInt32(data, ref idx); // not quite sure what this is about

        // If the IKE header is there, pass back to the ike handler.
        // We strip the padding off, and pass a flag to say it should be sent with a response
        if (header == IkeHeader) // start session?
        {
            Console.WriteLine("    SPI zero on 4500 -- sending to 500 (IKE) responder");
            var offsetData = data.Skip(4).ToArray();
            IkeSessionResponder(offsetData, sender, sendZeroHeader: true);
            return;
        }

        // Read the SPI? IEB: not sure about this. The reference is weird.
        var spi = Bit.ReadUInt64(data, ref idx);
        
        // reject unknown sessions
        if (!_sessions.ContainsKey(spi))
        {
            Console.WriteLine($"    Unknown session: 0x{spi:x16} -- not replying");
            return;
        }

        // if we get here, we have a new message (with encryption) for an existing session
        var session = _sessions[spi];
        idx = 4;
        var seq = Bit.ReadUInt32(data, ref idx);
        Console.WriteLine($"    Packet has sequence #{seq}");
        if (session.OutOfSequence(seq))
        {
            Console.WriteLine($"    Received out of sequence packet: {seq} -- not replying");
            return;
        }
        
        // TODO: HMAC-SHA2-256-96 fix ?  See pvpn/server.py:411
        
        // verify the checksum
        var ok = session.VerifyMessage(data);
        if (!ok)
        {
            Console.WriteLine($"    Received packet with bad checksum: {seq} -- not replying");
            return;
        }
        
        // looks ok. Step the sequence number forward
        session.IncrementSequence(seq);

        // do decrypt, route, etc.
        session.HandleSpe(data, sender);
        
        Console.WriteLine("    Looks like a fully valid message. Other side will expect a reply.");
    }



    public void Dispose()
    {
        _server.Dispose();
        GC.SuppressFinalize(this);
    }
}