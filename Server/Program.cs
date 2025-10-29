
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ChatServer
{
    /// <summary>
    /// Represents a chat message for logging and history
    /// </summary>
    class ChatMessage
    {
        public DateTime Timestamp { get; }
        public string Username { get; }
        public string Content { get; }
        public string Type { get; } // "chat", "whisper", "system", "command"

        public ChatMessage(string username, string content, string type = "chat")
        {
            Timestamp = DateTime.UtcNow;
            Username = username;
            Content = content;
            Type = type;
        }

        public override string ToString()
        {
            return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Type.ToUpper()}] {Username}: {Content}";
        }
    }

    /// <summary>
    /// Represents a connected client with their TCP connection, username, and moderator status
    /// </summary>
    class ClientInfo
    {
        /// <summary>TCP connection to the client</summary>
        public TcpClient Tcp { get; }

        /// <summary>Client's chosen username (empty until set)</summary>
        public string Username { get; set; } = "";

        /// <summary>Whether this client has moderator privileges</summary>
        public bool IsModerator { get; set; } = false;

        /// <summary>When this client connected</summary>
        public DateTime ConnectedAt { get; }

        /// <summary>
        /// Creates a new ClientInfo instance for a connected client
        /// </summary>
        /// <param name="tcp">The TCP connection to the client</param>
        public ClientInfo(TcpClient tcp)
        {
            Tcp = tcp;
            ConnectedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Main chat server class that handles client connections, message broadcasting,
    /// command processing, and moderator management
    /// </summary>
    class Server
    {
        // Core networking components
        private readonly TcpListener _listener;
        private readonly ConcurrentDictionary<TcpClient, ClientInfo> _clients = new();
        private readonly HashSet<string> _usernames = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _unameLock = new();
        private DateTime _startTime = DateTime.UtcNow;
        private volatile bool _running = true;

        // Message logging and history
        private readonly List<ChatMessage> _messageHistory = new();
        private readonly object _historyLock = new();
        private readonly string _logFilePath;

        /// <summary>
        /// Initializes a new server instance on the specified port
        /// </summary>
        /// <param name="port">Port number to listen on</param>
        public Server(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);

            // Create logs directory if it doesn't exist
            var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            if (!Directory.Exists(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }

            _logFilePath = Path.Combine(logsDir, $"chat_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            // Log server startup
            LogMessage("SERVER", "Chat server started", "system");
        }

        /// <summary>
        /// Starts the chat server and begins accepting client connections.
        /// Runs the main server loop for console commands.
        /// </summary>
        public void Start()
        {
            _listener.Start();
            Console.WriteLine($"[server] Listening on port {((_listener.LocalEndpoint as IPEndPoint)?.Port ?? 0)}");

            // Start background thread to accept incoming client connections
            new Thread(AcceptLoop) { IsBackground = true }.Start();

            // Main server console command loop for administrative commands
            Console.WriteLine("[server] Type !mods, !mod <user>, !kick <user> [reason], !shutdown");
            while (_running)
            {
                string? line = Console.ReadLine();
                if (line == null) break;
                HandleServerCommand(line);
            }
        }

        /// <summary>
        /// Background thread that continuously accepts new client connections.
        /// Each new client gets their own thread for message handling.
        /// </summary>
        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    // Wait for and accept new client connection
                    var tcp = _listener.AcceptTcpClient();
                    Console.WriteLine("[server] Incoming connection...");

                    // Create client info and add to active clients list
                    var ci = new ClientInfo(tcp);
                    _clients[tcp] = ci;

                    // Start a new thread to handle this client's messages
                    var th = new Thread(() => ClientLoop(ci)) { IsBackground = true };
                    th.Start();
                }
                catch (SocketException)
                {
                    // Expected when server shuts down
                    if (!_running) break;
                }
            }
        }

        /// <summary>
        /// Reads a line of text from the network stream until newline character
        /// </summary>
        /// <param name="ns">Network stream to read from</param>
        /// <returns>Line of text read from the stream</returns>
        private static string ReadLine(NetworkStream ns)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = ns.ReadByte()) != -1)
            {
                if (b == (int)'\n') break;  // End of line found
                if (b != '\r') sb.Append((char)b);  // Skip carriage return, keep other chars
            }
            return sb.ToString();
        }

        /// <summary>
        /// Writes a line of text to the network stream with proper line ending
        /// </summary>
        /// <param name="ns">Network stream to write to</param>
        /// <param name="line">Text line to send</param>
        private static void WriteLine(NetworkStream ns, string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            ns.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Main message handling loop for a connected client.
        /// Handles username setup, command processing, and message broadcasting.
        /// </summary>
        /// <param name="ci">Client information for this connection</param>
        private void ClientLoop(ClientInfo ci)
        {
            var ns = ci.Tcp.GetStream();
            WriteLine(ns, "Welcome to the chat server. Please set your username with !username <name>.");

            try
            {
                // Phase 1: Username setup - client must set username before chatting
                while (string.IsNullOrWhiteSpace(ci.Username))
                {
                    string first = ReadLine(ns);

                    // Validate that client starts with username command
                    if (!first.StartsWith("!username ", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteLine(ns, "ERROR: You must start with !username <name>");
                        continue;
                    }

                    // Extract and validate username
                    var name = first.Substring("!username ".Length).Trim();
                    if (!IsValidUsername(name))
                    {
                        WriteLine(ns, "ERROR: Invalid username. Must be 3-20 characters, start with letter/number, and contain only letters, numbers, underscore, or hyphen.");
                        continue;
                    }

                    // Check if username is available
                    if (!TryAddUsername(name))
                    {
                        WriteLine(ns, "ERROR: Username already in use. Disconnecting.");
                        break;
                    }

                    // Username successfully set
                    ci.Username = name;
                    WriteLine(ns, $"OK: Welcome {ci.Username}!");
                    Broadcast($"* {ci.Username} joined the chat *", exclude: ci);
                }

                // If username setup failed, disconnect client
                if (string.IsNullOrWhiteSpace(ci.Username))
                {
                    return;
                }

                // Phase 2: Main chat loop - handle messages and commands
                WriteLine(ns, "Type !commands for help.");
                while (_running && ci.Tcp.Connected)
                {
                    string line = ReadLine(ns);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.StartsWith("!"))
                    {
                        // Process command (whisper, who, about, etc.)
                        HandleClientCommand(ci, line);
                    }
                    else
                    {
                        // Broadcast regular chat message to all clients
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
                // Always clean up connection when client disconnects
                Disconnect(ci, notify: true, reason: "left the chat");
            }
        }

        /// <summary>
        /// Processes client commands like !who, !about, !whisper, !user, !kick, etc.
        /// </summary>
        /// <param name="ci">Client sending the command</param>
        /// <param name="cmd">Command string to process</param>
        private void HandleClientCommand(ClientInfo ci, string cmd)
        {
            var ns = ci.Tcp.GetStream();
            string[] parts = cmd.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            string root = parts[0].ToLowerInvariant();

            switch (root)
            {
                case "!commands":
                    WriteLine(ns, "Commands: !who, !about, !whisper <user> <msg>, !w <user> <msg>, !user <newname>, !ping, !stats");
                    if (ci.IsModerator)
                    {
                        WriteLine(ns, "Moderator: !kick <user> [reason], !history");
                    }
                    else
                    {
                        WriteLine(ns, "Moderator commands: !kick <user> [reason], !history (moderator only)");
                    }
                    break;

                case "!who":
                    var names = _clients.Values.Where(c => !string.IsNullOrWhiteSpace(c.Username)).Select(c => c.Username).OrderBy(s => s).ToArray();
                    WriteLine(ns, "Connected users: " + (names.Length == 0 ? "(none)" : string.Join(", ", names)));
                    break;

                case "!about":
                    WriteLine(ns, "Sonny's Chat Server: creator: Sonny, purpose: learning sockets, year: 2025");
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
                    if (!KickUser(parts[1], reason, ci))
                    {
                        WriteLine(ns, $"ERROR: User '{parts[1]}' not found.");
                    }
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

                case "!history":
                    if (!ci.IsModerator)
                    {
                        WriteLine(ns, "ERROR: Only moderators can view chat history.");
                        break;
                    }
                    var history = GetRecentHistory(10);
                    WriteLine(ns, "Recent chat history:");
                    foreach (var msg in history)
                    {
                        WriteLine(ns, $"  {msg}");
                    }
                    break;

                default:
                    WriteLine(ns, "Unknown command. Try !commands");
                    break;
            }
        }

        /// <summary>
        /// Sends a private message from one client to another
        /// </summary>
        /// <param name="from">Client sending the whisper</param>
        /// <param name="toUser">Username of the target client</param>
        /// <param name="message">Message content to send</param>
        private void Whisper(ClientInfo from, string toUser, string message)
        {
            // Find target user by username (case-insensitive)
            var target = _clients.Values.FirstOrDefault(c => string.Equals(c.Username, toUser, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                WriteLine(from.Tcp.GetStream(), $"User '{toUser}' not found.");
                return;
            }

            // Log the whisper message
            LogMessage(from.Username, $"whisper to {toUser}: {message}", "whisper");

            // Send message to both sender and recipient for confirmation
            WriteLine(target.Tcp.GetStream(), $"[whisper from {from.Username}]: {message}");
            WriteLine(from.Tcp.GetStream(), $"[whisper to {target.Username}]: {message}");
        }

        /// <summary>
        /// Validates username format and characters
        /// </summary>
        /// <param name="username">Username to validate</param>
        /// <returns>True if username is valid, false otherwise</returns>
        private static bool IsValidUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Contains(' '))
                return false;

            // Check for invalid characters (only allow alphanumeric, underscore, and hyphen)
            foreach (char c in username)
            {
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                    return false;
            }

            // Username must start with letter or number
            if (username.Length > 0 && !char.IsLetterOrDigit(username[0]))
                return false;

            // Username length limits
            if (username.Length < 3 || username.Length > 20)
                return false;

            return true;
        }

        /// <summary>
        /// Changes a client's username with validation and atomic updates
        /// </summary>
        /// <param name="ci">Client changing their username</param>
        /// <param name="newName">New username to set</param>
        private void ChangeUsername(ClientInfo ci, string newName)
        {
            // Validate new username format
            if (!IsValidUsername(newName))
            {
                WriteLine(ci.Tcp.GetStream(), "ERROR: Invalid username. Must be 3-20 characters, start with letter/number, and contain only letters, numbers, underscore, or hyphen.");
                return;
            }

            // Thread-safe username management
            lock (_unameLock)
            {
                // Check if new username is already taken
                if (_usernames.Contains(newName))
                {
                    WriteLine(ci.Tcp.GetStream(), "ERROR: Username already in use.");
                    return;
                }

                // Atomically swap usernames: remove old, add new
                if (!string.IsNullOrWhiteSpace(ci.Username)) _usernames.Remove(ci.Username);
                _usernames.Add(newName);
                var old = ci.Username;
                ci.Username = newName;

                // Notify all clients of the username change
                Broadcast($"* {old} is now known as {ci.Username} *");
            }
        }

        /// <summary>
        /// Thread-safely adds a username to the global username set
        /// </summary>
        /// <param name="name">Username to add</param>
        /// <returns>True if username was added, false if already exists</returns>
        private bool TryAddUsername(string name)
        {
            lock (_unameLock)
            {
                if (_usernames.Contains(name)) return false;
                _usernames.Add(name);
                return true;
            }
        }

        /// <summary>
        /// Logs a message to history and file
        /// </summary>
        /// <param name="username">Username of the sender</param>
        /// <param name="content">Message content</param>
        /// <param name="type">Type of message (chat, whisper, system, command)</param>
        private void LogMessage(string username, string content, string type = "chat")
        {
            var msg = new ChatMessage(username, content, type);

            // Add to in-memory history (keep last 1000 messages)
            lock (_historyLock)
            {
                _messageHistory.Add(msg);
                if (_messageHistory.Count > 1000)
                {
                    _messageHistory.RemoveAt(0);
                }
            }

            // Write to log file
            try
            {
                File.AppendAllText(_logFilePath, msg.ToString() + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[server] Log write error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets recent message history for a client
        /// </summary>
        /// <param name="count">Number of recent messages to retrieve</param>
        /// <returns>Array of recent messages</returns>
        private ChatMessage[] GetRecentHistory(int count = 10)
        {
            lock (_historyLock)
            {
                return _messageHistory.TakeLast(count).ToArray();
            }
        }

        /// <summary>
        /// Broadcasts a message to all connected clients except optionally excluded one
        /// </summary>
        /// <param name="message">Message to broadcast</param>
        /// <param name="exclude">Optional client to exclude from broadcast</param>
        private void Broadcast(string message, ClientInfo? exclude = null)
        {
            // Log the message if it's from a specific user
            if (exclude == null && message.Contains("]: "))
            {
                var parts = message.Split("]: ", 2);
                if (parts.Length == 2)
                {
                    var username = parts[0].Substring(1); // Remove the '['
                    LogMessage(username, parts[1], "chat");
                }
            }
            else if (exclude == null && message.StartsWith("* ") && message.EndsWith(" *"))
            {
                LogMessage("SYSTEM", message, "system");
            }

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
                    // Ignore write errors; connection will be cleaned up later
                }
            }
            Console.WriteLine(message);  // Also log to server console
        }

        /// <summary>
        /// Cleanly disconnects a client and removes them from all tracking structures
        /// </summary>
        /// <param name="ci">Client to disconnect</param>
        /// <param name="notify">Whether to broadcast disconnection message</param>
        /// <param name="reason">Reason for disconnection (for notification)</param>
        private void Disconnect(ClientInfo ci, bool notify, string reason)
        {
            if (!_clients.ContainsKey(ci.Tcp)) return;

            // Remove from active clients list
            _clients.TryRemove(ci.Tcp, out _);

            // Remove username from global set if set
            if (!string.IsNullOrWhiteSpace(ci.Username))
            {
                lock (_unameLock) { _usernames.Remove(ci.Username); }
            }

            // Close TCP connection
            try { ci.Tcp.Close(); } catch { }

            // Notify other clients if requested
            if (notify && !string.IsNullOrWhiteSpace(ci.Username))
            {
                Broadcast($"* {ci.Username} {reason} *");
            }
        }

        /// <summary>
        /// Kicks a user from the server with optional reason (from moderator)
        /// </summary>
        /// <param name="username">Username to kick</param>
        /// <param name="reason">Reason for kicking</param>
        /// <param name="moderator">The moderator who initiated the kick</param>
        /// <returns>True if user was found and kicked, false if user not found</returns>
        private bool KickUser(string username, string reason, ClientInfo moderator)
        {
            // Find target user by username
            var target = _clients.Values.FirstOrDefault(c => string.Equals(c.Username, username, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                Console.WriteLine($"[server] Kick failed; user '{username}' not found by moderator {moderator.Username}.");
                return false;
            }

            // Send kick notification to target client
            try
            {
                WriteLine(target.Tcp.GetStream(), $"!kicked {reason}");
            }
            catch { }

            // Disconnect the user
            Disconnect(target, notify: true, reason: $"was kicked by {moderator.Username} ({reason})");
            return true;
        }

        /// <summary>
        /// Kicks a user from the server with optional reason (from server console)
        /// </summary>
        /// <param name="username">Username to kick</param>
        /// <param name="reason">Reason for kicking</param>
        /// <returns>True if user was found and kicked, false if user not found</returns>
        private bool KickUserFromServer(string username, string reason)
        {
            // Find target user by username
            var target = _clients.Values.FirstOrDefault(c => string.Equals(c.Username, username, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                Console.WriteLine($"[server] Kick failed; user '{username}' not found.");
                return false;
            }

            // Send kick notification to target client
            try
            {
                WriteLine(target.Tcp.GetStream(), $"!kicked {reason}");
            }
            catch { }

            // Disconnect the user
            Disconnect(target, notify: true, reason: $"was kicked by server ({reason})");
            return true;
        }

        /// <summary>
        /// Handles server console commands for administration (moderator management, kicking, shutdown)
        /// </summary>
        /// <param name="line">Command line entered by server administrator</param>
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
                    if (!KickUserFromServer(parts[1], reason))
                    {
                        Console.WriteLine($"ERROR: User '{parts[1]}' not found.");
                    }
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

    /// <summary>
    /// Main program entry point for the chat server
    /// </summary>
    class Program
    {
        /// <summary>
        /// Application entry point - starts the chat server on specified or default port
        /// </summary>
        /// <param name="args">Command line arguments: [port] (optional, defaults to 5001)</param>
        static void Main(string[] args)
        {
            int port = 5001;
            if (args.Length >= 1 && int.TryParse(args[0], out var p)) port = p;
            var server = new Server(port);
            server.Start();
        }
    }
}
