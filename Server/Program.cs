
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ChatServer
{
    class ClientInfo
    {
        public TcpClient Tcp { get; }
        public string Username { get; set; } = "";
        public bool IsModerator { get; set; } = false;

        public ClientInfo(TcpClient tcp) => Tcp = tcp;
    }

    class Server
    {
        private readonly TcpListener _listener;
        private readonly ConcurrentDictionary<TcpClient, ClientInfo> _clients = new();
        private readonly HashSet<string> _usernames = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _unameLock = new();
        private DateTime _startTime = DateTime.UtcNow;
        private volatile bool _running = true;

        public Server(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
        }

        public void Start()
        {
            _listener.Start();
            Console.WriteLine($"[server] Listening on port {((_listener.LocalEndpoint as IPEndPoint)?.Port ?? 0)}");

            // Accept loop
            new Thread(AcceptLoop) { IsBackground = true }.Start();

            // Server console command loop
            Console.WriteLine("[server] Type !mods, !mod <user>, !kick <user> [reason], !shutdown");
            while (_running)
            {
                string? line = Console.ReadLine();
                if (line == null) break;
                HandleServerCommand(line);
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var tcp = _listener.AcceptTcpClient();
                    Console.WriteLine("[server] Incoming connection...");
                    var ci = new ClientInfo(tcp);
                    _clients[tcp] = ci;

                    var th = new Thread(() => ClientLoop(ci)) { IsBackground = true };
                    th.Start();
                }
                catch (SocketException)
                {
                    if (!_running) break;
                }
            }
        }

        private static string ReadLine(NetworkStream ns)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = ns.ReadByte()) != -1)
            {
                if (b == (int)'\n') break;
                if (b != '\r') sb.Append((char)b);
            }
            return sb.ToString();
        }

        private static void WriteLine(NetworkStream ns, string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            ns.Write(bytes, 0, bytes.Length);
        }

        private void ClientLoop(ClientInfo ci)
        {
            var ns = ci.Tcp.GetStream();
            WriteLine(ns, "Welcome to the chat server. Please set your username with !username <name>.");

            try
            {
                // Expect username first
                while (string.IsNullOrWhiteSpace(ci.Username))
                {
                    string first = ReadLine(ns);
                    if (!first.StartsWith("!username ", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteLine(ns, "ERROR: You must start with !username <name>");
                        continue;
                    }
                    var name = first.Substring("!username ".Length).Trim();
                    if (string.IsNullOrWhiteSpace(name) || name.Contains(' '))
                    {
                        WriteLine(ns, "ERROR: Invalid username (no spaces, non-empty).");
                        continue;
                    }
                    if (!TryAddUsername(name))
                    {
                        WriteLine(ns, "ERROR: Username already in use. Disconnecting.");
                        break;
                    }
                    ci.Username = name;
                    WriteLine(ns, $"OK: Welcome {ci.Username}!");
                    Broadcast($"* {ci.Username} joined the chat *", exclude: ci);
                }

                if (string.IsNullOrWhiteSpace(ci.Username))
                {
                    // failed to set username; disconnect
                    return;
                }

                // Main loop
                WriteLine(ns, "Type !commands for help.");
                while (_running && ci.Tcp.Connected)
                {
                    string line = ReadLine(ns);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.StartsWith("!"))
                    {
                        HandleClientCommand(ci, line);
                    }
                    else
                    {
                        Broadcast($"[{ci.Username}]: {line}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[server] Client loop error: {ex.Message}");
            }
            finally
            {
                Disconnect(ci, notify: true, reason: "left the chat");
            }
        }

        private void HandleClientCommand(ClientInfo ci, string cmd)
        {
            var ns = ci.Tcp.GetStream();
            string[] parts = cmd.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            string root = parts[0].ToLowerInvariant();

            switch (root)
            {
                case "!commands":
                    WriteLine(ns, "Commands: !who, !about, !whisper <user> <msg>, !w <user> <msg>, !user <newname>, !ping, !stats");
                    if (ci.IsModerator) WriteLine(ns, "Moderator: !kick <user> [reason]");
                    break;

                case "!who":
                    var names = _clients.Values.Where(c => !string.IsNullOrWhiteSpace(c.Username)).Select(c => c.Username).OrderBy(s => s).ToArray();
                    WriteLine(ns, "Connected users: " + (names.Length == 0 ? "(none)" : string.Join(", ", names)));
                    break;

                case "!about":
                    WriteLine(ns, "NDS203 Chat Server — creator: Sonny, purpose: learning sockets, year: 2025");
                    break;

                case "!whisper":
                case "!w":
                    if (parts.Length < 3) { WriteLine(ns, "Usage: !whisper <username> <message>"); break; }
                    Whisper(ci, parts[1], parts[2]);
                    break;

                case "!user":
                    if (parts.Length < 2) { WriteLine(ns, "Usage: !user <newname>"); break; }
                    ChangeUsername(ci, parts[1]);
                    break;

                case "!kick":
                    if (!ci.IsModerator) { WriteLine(ns, "ERROR: Only moderators can use !kick"); break; }
                    if (parts.Length < 2) { WriteLine(ns, "Usage: !kick <username> [reason]"); break; }
                    var reason = parts.Length == 3 ? parts[2] : "Kicked by moderator";
                    KickUser(parts[1], reason);
                    break;
                //Custom commands
                case "!ping":
                    WriteLine(ns, "pong");
                    break;

                case "!stats":
                    var totalUsers = _clients.Count;
                    var onlineUsers = _clients.Values.Count(c => !string.IsNullOrWhiteSpace(c.Username));
                    var moderators = _clients.Values.Count(c => c.IsModerator);
                    var up = DateTime.UtcNow - _startTime;
                    WriteLine(ns, $"Server Stats: {onlineUsers} online users, {moderators} moderators, uptime: {up:dd\\.hh\\:mm\\:ss}");
                    break;

                default:
                    WriteLine(ns, "Unknown command. Try !commands");
                    break;
            }
        }

        private void Whisper(ClientInfo from, string toUser, string message)
        {
            var target = _clients.Values.FirstOrDefault(c => string.Equals(c.Username, toUser, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                WriteLine(from.Tcp.GetStream(), $"User '{toUser}' not found.");
                return;
            }
            WriteLine(target.Tcp.GetStream(), $"[whisper from {from.Username}]: {message}");
            WriteLine(from.Tcp.GetStream(), $"[whisper to {target.Username}]: {message}");
        }

        private void ChangeUsername(ClientInfo ci, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName) || newName.Contains(' '))
            {
                WriteLine(ci.Tcp.GetStream(), "ERROR: Invalid username (no spaces, non-empty).");
                return;
            }
            lock (_unameLock)
            {
                if (_usernames.Contains(newName))
                {
                    WriteLine(ci.Tcp.GetStream(), "ERROR: Username already in use.");
                    return;
                }
                // swap names
                if (!string.IsNullOrWhiteSpace(ci.Username)) _usernames.Remove(ci.Username);
                _usernames.Add(newName);
                var old = ci.Username;
                ci.Username = newName;
                Broadcast($"* {old} is now known as {ci.Username} *");
            }
        }

        private bool TryAddUsername(string name)
        {
            lock (_unameLock)
            {
                if (_usernames.Contains(name)) return false;
                _usernames.Add(name);
                return true;
            }
        }

        private void Broadcast(string message, ClientInfo? exclude = null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message + "\r\n");
            foreach (var kv in _clients.ToArray())
            {
                var ci = kv.Value;
                if (exclude != null && ReferenceEquals(ci, exclude)) continue;
                try
                {
                    var ns = ci.Tcp.GetStream();
                    ns.Write(bytes, 0, bytes.Length);
                }
                catch
                {
                    // ignore write errors; clean up on next loop
                }
            }
            Console.WriteLine(message);
        }

        private void Disconnect(ClientInfo ci, bool notify, string reason)
        {
            if (!_clients.ContainsKey(ci.Tcp)) return;

            _clients.TryRemove(ci.Tcp, out _);
            if (!string.IsNullOrWhiteSpace(ci.Username))
            {
                lock (_unameLock) { _usernames.Remove(ci.Username); }
            }
            try { ci.Tcp.Close(); } catch { }

            if (notify && !string.IsNullOrWhiteSpace(ci.Username))
            {
                Broadcast($"* {ci.Username} {reason} *");
            }
        }

        private void KickUser(string username, string reason)
        {
            var target = _clients.Values.FirstOrDefault(c => string.Equals(c.Username, username, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                Console.WriteLine($"[server] Kick failed; user '{username}' not found.");
                return;
            }
            try
            {
                WriteLine(target.Tcp.GetStream(), $"!kicked {reason}");
            }
            catch { }
            Disconnect(target, notify: true, reason: $"was kicked ({reason})");
        }

        private void HandleServerCommand(string line)
        {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var root = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

            switch (root)
            {
                case "!mods":
                    var mods = _clients.Values.Where(c => c.IsModerator).Select(c => c.Username).OrderBy(s => s).ToArray();
                    Console.WriteLine("Moderators: " + (mods.Length == 0 ? "(none)" : string.Join(", ", mods)));
                    break;

                case "!mod":
                    if (parts.Length < 2) { Console.WriteLine("Usage: !mod <username>"); break; }
                    var user = parts[1];
                    var ci = _clients.Values.FirstOrDefault(c => string.Equals(c.Username, user, StringComparison.OrdinalIgnoreCase));
                    if (ci == null) { Console.WriteLine("No such user."); break; }
                    ci.IsModerator = !ci.IsModerator;
                    Broadcast($"* {ci.Username} is {(ci.IsModerator ? "now a moderator" : "no longer a moderator")} *");
                    break;

                case "!kick":
                    if (parts.Length < 2) { Console.WriteLine("Usage: !kick <username> [reason]"); break; }
                    var reason = parts.Length == 3 ? parts[2] : "Kicked by server";
                    KickUser(parts[1], reason);
                    break;

                case "!shutdown":
                    Console.WriteLine("[server] Shutting down...");
                    _running = false;
                    try { _listener.Stop(); } catch { }
                    foreach (var c in _clients.Values.ToList()) Disconnect(c, notify: true, reason: "server is shutting down");
                    Environment.Exit(0);
                    break;

                default:
                    if (!string.IsNullOrWhiteSpace(line))
                        Console.WriteLine("Unknown server command. Try !mods, !mod <user>, !kick <user> [reason], !shutdown");
                    break;
            }
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            int port = 5001;
            if (args.Length >= 1 && int.TryParse(args[0], out var p)) port = p;
            var server = new Server(port);
            server.Start();
        }
    }
}
