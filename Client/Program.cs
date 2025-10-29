
using System;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;

namespace ChatClient
{
    /// <summary>
    /// Chat client application that connects to the chat server
    /// </summary>
    class Program
    {
        /// <summary>
        /// Main entry point for the chat client
        /// </summary>
        /// <param name="args">Command line arguments: [host] [port] (both optional)</param>
        static void Main(string[] args)
        {
            // Parse command line arguments for host and port
            string host = args.Length >= 1 ? args[0] : "127.0.0.1";
            int port = 5001;
            if (args.Length >= 2 && int.TryParse(args[1], out var p)) port = p;

            // Get username from user or generate random one
            Console.Write("Enter desired username: ");
            string? uname = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(uname)) uname = "User" + new Random().Next(1000, 9999);

            try
            {
                // Establish TCP connection to server
                using var tcp = new TcpClient();
                tcp.Connect(host, port);
                Console.WriteLine($"[client] Connected to {host}:{port}");
                var ns = tcp.GetStream();

                // Background thread to read messages from server
                var reader = new Thread(() =>
                {
                    try
                    {
                        var buf = new byte[1];
                        var line = new StringBuilder();
                        while (true)
                        {
                            int b = ns.ReadByte();
                            if (b == -1) break;  // Connection closed

                            if (b == (int)'\n')  // End of line
                            {
                                string s = line.ToString();
                                line.Clear();

                                // Handle special kick message from server
                                if (s.StartsWith("!kicked "))
                                {
                                    Console.WriteLine($"[server] {s.Substring(8)}");
                                    Console.WriteLine("[client] Disconnected.");
                                    Environment.Exit(0);
                                }
                                else
                                {
                                    // Display regular message
                                    Console.WriteLine(s);
                                }
                            }
                            else if (b != (int)'\r')  // Skip carriage return
                            {
                                line.Append((char)b);
                            }
                        }
                    }
                    catch { }  // Ignore read errors
                });
                reader.IsBackground = true;
                reader.Start();

                // Send username to server
                WriteLine(ns, $"!username {uname}");

                // Main input loop - read user input and send to server
                while (true)
                {
                    string? input = Console.ReadLine();
                    if (input == null) break;

                    // Check for quit command
                    if (string.Equals(input, "!quit", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("[client] Bye.");
                        break;
                    }

                    // Send message to server
                    WriteLine(ns, input);
                }
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[client] Socket error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[client] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a line of text to the server over the network stream
        /// </summary>
        /// <param name="ns">Network stream connected to server</param>
        /// <param name="line">Text line to send</param>
        private static void WriteLine(NetworkStream ns, string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            ns.Write(bytes, 0, bytes.Length);
        }
    }
}
