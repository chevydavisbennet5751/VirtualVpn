﻿// See https://aka.ms/new-console-template for more information

using System.Net;
using System.Net.Sockets;
using System.Text;

Console.WriteLine("I will listen for UDP messages on ports 500 and 4500 and list them...");


var commsThreadIke = new Thread(IkeLoop){IsBackground = true};
var commsThreadSpe = new Thread(SpeLoop){IsBackground = true};
commsThreadIke.Start();
commsThreadSpe.Start();


void IkeLoop()
{
    var buffer = new byte[1024];
    var ipep = new IPEndPoint(IPAddress.Any, 500);
    using var newsock = new UdpClient(ipep);

    Console.WriteLine("Listen on 500...");

    var sender = new IPEndPoint(IPAddress.Any, 0);

    while(true)
    {
        buffer = newsock.Receive(ref sender);

        Console.WriteLine("P500:  "+Encoding.ASCII.GetString(buffer, 0, buffer.Length));
        newsock.Send(buffer, buffer.Length, sender);
    }
}
    
void SpeLoop()
{
    var buffer = new byte[1024];
    var ipep = new IPEndPoint(IPAddress.Any, 4500);
    using var newsock = new UdpClient(ipep);

    Console.WriteLine("Listen on 4500...");

    var sender = new IPEndPoint(IPAddress.Any, 0);

    while(true)
    {
        buffer = newsock.Receive(ref sender);

        Console.WriteLine("P4500: "+Encoding.ASCII.GetString(buffer, 0, buffer.Length));
        newsock.Send(buffer, buffer.Length, sender);
    }
}