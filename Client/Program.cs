
using System;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;

namespace ChatClient
{
    class Program
    {
        static void Main(string[] args)
        {
            string host = args.Length >= 1 ? args[0] : "127.0.0.1";
            int port = 5001;
            if (args.Length >= 2 && int.TryParse(args[1], out var p)) port = p;

            Console.Write("Enter desired username: ");
            string? uname = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(uname)) uname = "User" + new Random().Next(1000, 9999);

            try
            {
                using var tcp = new TcpClient();
                tcp.Connect(host, port);
                Console.WriteLine($"[client] Connected to {host}:{port}");
                var ns = tcp.GetStream();

                // Reader thread
                var reader = new Thread(() =>
                {
                    try
                    {
                        var buf = new byte[1];
                        var line = new StringBuilder();
                        while (true)
                        {
                            int b = ns.ReadByte();
                            if (b == -1) break;
                            if (b == (int)'\n')
                            {
                                string s = line.ToString();
                                line.Clear();

                                if (s.StartsWith("!kicked "))
                                {
                                    Console.WriteLine($"[server] {s.Substring(8)}");
                                    Console.WriteLine("[client] Disconnected.");
                                    Environment.Exit(0);
                                }
                                else
                                {
                                    Console.WriteLine(s);
                                }
                            }
                            else if (b != (int)'\r')
                            {
                                line.Append((char)b);
                            }
                        }
                    }
                    catch { }
                });
                reader.IsBackground = true;
                reader.Start();

                // Send username
                WriteLine(ns, $"!username {uname}");

                // Input loop
                while (true)
                {
                    string? input = Console.ReadLine();
                    if (input == null) break;
                    if (string.Equals(input, "!quit", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("[client] Bye.");
                        break;
                    }
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

        private static void WriteLine(NetworkStream ns, string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            ns.Write(bytes, 0, bytes.Length);
        }
    }
}
