# Chat Server & Client Application

A TCP-based chat server and client application built with .NET 6.0, featuring AI-powered content moderation using Google Gemini.

## Prerequisites

- .NET 6.0 SDK or later
- (Optional) Google Gemini API key for bad word detection

## Quick Start

### 1. Setup Environment (Optional - for AI moderation)

Create a `.env` file in the `Server` directory:

```bash
cd Server
cp .env.example .env
```

Edit `.env` and add your Gemini API key:
```
GEMINI_API_KEY=your_api_key_here
```

Get your API key from: https://makersuite.google.com/app/apikey

### 2. Run the Server

```bash
cd Server
dotnet run
```

Or specify a custom port:
```bash
dotnet run 8080
```

Default port: **5001**

### 3. Run the Client

In a new terminal:

```bash
cd Client
dotnet run
```

Or connect to a specific host/port:
```bash
dotnet run 127.0.0.1 5001
```

When prompted, enter your desired username (must start with `!username <name>`).

## Commands Reference

### Client Commands

All commands start with `!`

| Command | Description | Usage | Example |
|---------|-----------|-------|---------|
| `!commands` | Show all available commands | `!commands` | `!commands` |
| `!who` | List all connected users | `!who` | `!who` |
| `!about` | Show server information | `!about` | `!about` |
| `!whisper` or `!w` | Send private message to a user | `!whisper <user> <message>` | `!whisper john Hello!` |
| `!user` | Change your username | `!user <newname>` | `!user newname` |
| `!ping` | Test connectivity (returns "pong") | `!ping` | `!ping` |
| `!stats` | Show server statistics | `!stats` | `!stats` |
| `!kick` | Kick a user (Moderator only) | `!kick <user> [reason]` | `!kick john Spamming` |
| `!history` | View recent chat history (Moderator only) | `!history` | `!history` |
| `!quit` | Disconnect from server | `!quit` | `!quit` |

### Server Console Commands

Commands entered in the server console:

| Command | Description | Usage | Example |
|---------|-----------|-------|---------|
| `!mods` | List all moderators | `!mods` | `!mods` |
| `!mod` | Toggle moderator status for a user | `!mod <username>` | `!mod john` |
| `!kick` | Kick a user from server | `!kick <user> [reason]` | `!kick john Spamming` |
| `!shutdown` | Shutdown the server | `!shutdown` | `!shutdown` |

## Features

### Core Features
- TCP-based chat server and client
- Multi-user chat room
- Private messaging (whisper)
- Username management
- Moderator system
- User kicking functionality
- Message history and logging

### Advanced Features
- **AI-Powered Content Moderation** - Detects inappropriate content using Google Gemini AI
- **Automatic Moderator Alerts** - Notifies all moderators when bad words are detected
- **Message Logging** - All messages logged to files in `logs/` directory
- **Server Statistics** - View online users, moderators, and uptime
- **Chat History** - Moderators can view recent message history

## Project Structure

```
NDS203_SonnyNguyen_ A00188041_Assessment2/
├── Server/
│   ├── Program.cs          # Server implementation
│   ├── Server.csproj       # Server project file
│   ├── .env.example        # Environment template
│   └── logs/               # Chat log files
├── Client/
│   ├── Program.cs          # Client implementation
│   └── Client.csproj       # Client project file
├── AddedFunctions.md       # Detailed feature documentation
└── README.md               # This file
```

## Configuration

### Environment Variables

- `GEMINI_API_KEY` - Google Gemini API key for bad word detection (optional)
  - If not set, bad word detection is disabled but server continues normally
  - Can be set via `.env` file or system environment variable

### Server Port

- Default: `5001`
- Custom: Pass as command line argument: `dotnet run <port>`

## Usage Examples

### Starting Server on Custom Port
```bash
cd Server
dotnet run 8080
```

### Connecting Client to Remote Server
```bash
cd Client
dotnet run 192.168.1.100 5001
```

### Making a User a Moderator
In server console:
```
!mod john
```

### Sending a Private Message
In client:
```
!whisper john Hello, how are you?
```

### Viewing Server Stats
In client:
```
!stats
```

## Notes

- Usernames must be 3-20 characters, start with letter/number, and contain only alphanumeric, underscore, or hyphen
- Messages are checked for inappropriate content before broadcasting
- Moderators receive alerts when bad words are detected
- All chat activity is logged to files in `Server/logs/` directory
- Server must be running before clients can connect

## Troubleshooting

**Server won't start:**
- Check if port is already in use
- Ensure .NET 6.0 SDK is installed

**Client can't connect:**
- Verify server is running
- Check host and port are correct
- Ensure firewall allows the connection

**AI moderation not working:**
- Check `.env` file exists and contains valid `GEMINI_API_KEY`
- Verify API key is valid and has proper permissions
- Check server console for error messages

## License

This project is created for educational purposes.

