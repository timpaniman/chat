using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

public class ChatRoomServer
{
    const int MaxHistory = 200;
    const int MaxText = 2000;

    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static readonly object Sync = new object();
    static readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
    static readonly Dictionary<string, WebSocket> Members = new Dictionary<string, WebSocket>();
    static readonly Dictionary<WebSocket, string> Ids = new Dictionary<WebSocket, string>();
    static readonly List<Dictionary<string, object>> History = new List<Dictionary<string, object>>();
    static readonly Regex IdPattern = new Regex("^[A-Za-z0-9_-]{1,24}$");

    static string PublicDir;

    public static void Run(string publicDir, int port)
    {
        PublicDir = Path.GetFullPath(publicDir);
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:" + port + "/");
        listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        listener.Start();
        Console.WriteLine("Chat room listening on http://localhost:" + port);
        Task.Run(() => Sweep());

        while (listener.IsListening)
        {
            HttpListenerContext context = listener.GetContext();
            Task.Run(() => Handle(context));
        }
    }

    static async Task Handle(HttpListenerContext context)
    {
        try
        {
            if (context.Request.IsWebSocketRequest)
            {
                HttpListenerWebSocketContext accepted = await context.AcceptWebSocketAsync(null);
                await Talk(accepted.WebSocket);
                return;
            }

            Serve(context);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    static void Serve(HttpListenerContext context)
    {
        string absolutePath = context.Request.Url.AbsolutePath;
        string requested = absolutePath == "/" ? "index.html" : Uri.UnescapeDataString(absolutePath).TrimStart('/');
        string root = PublicDir;
        string full = Path.GetFullPath(Path.Combine(root, requested.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            WriteText(context, 403, "Forbidden");
            return;
        }

        if (!File.Exists(full))
        {
            WriteText(context, 404, "Not found");
            return;
        }

        byte[] bytes = File.ReadAllBytes(full);
        context.Response.StatusCode = 200;
        context.Response.ContentType = ContentType(full);
        context.Response.ContentLength64 = bytes.Length;
        context.Response.AddHeader("Cache-Control", "no-cache");
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    static void WriteText(HttpListenerContext context, int status, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    static string ContentType(string full)
    {
        string ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext == ".html") return "text/html; charset=utf-8";
        if (ext == ".css") return "text/css; charset=utf-8";
        if (ext == ".js") return "text/javascript; charset=utf-8";
        return "application/octet-stream";
    }

    static async Task Talk(WebSocket socket)
    {
        byte[] buffer = new byte[16384];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                MemoryStream message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 16384)
                    {
                        return;
                    }
                }
                while (!result.EndOfMessage);

                string text = Encoding.UTF8.GetString(message.ToArray());
                await OnMessage(socket, text);
            }
        }
        catch (Exception)
        {
        }

        await Leave(socket);
        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }
        catch (Exception)
        {
        }
    }

    static async Task OnMessage(WebSocket socket, string text)
    {
        Dictionary<string, object> data = null;
        bool parsed = true;
        try
        {
            data = Json.Deserialize<Dictionary<string, object>>(text);
        }
        catch (Exception)
        {
            parsed = false;
        }

        if (!parsed || data == null)
        {
            await Send(socket, Error("That message could not be read."));
            return;
        }

        string type = Field(data, "type");
        if (type == "join")
        {
            await Join(socket, Field(data, "userId").Trim());
            return;
        }

        string userId = CurrentId(socket);
        if (userId == null)
        {
            await Send(socket, Error("Join the room before sending."));
            return;
        }

        if (type != "chat") return;

        string body = Field(data, "text").Trim();
        if (body.Length == 0)
        {
            await Send(socket, Error("Write a message before sending."));
            return;
        }
        if (body.Length > MaxText)
        {
            await Send(socket, Error("Keep messages under " + MaxText + " characters."));
            return;
        }

        Dictionary<string, object> message = new Dictionary<string, object>();
        message["id"] = Guid.NewGuid().ToString();
        message["userId"] = userId;
        message["text"] = body;
        message["ts"] = Now();

        lock (Sync)
        {
            History.Add(message);
            if (History.Count > MaxHistory) History.RemoveAt(0);
        }

        Dictionary<string, object> payload = new Dictionary<string, object>();
        payload["type"] = "chat";
        payload["message"] = message;
        await Send(socket, payload);
        await Broadcast(payload, socket);
    }

    static async Task Join(WebSocket socket, string userId)
    {
        if (!IdPattern.IsMatch(userId))
        {
            await Send(socket, Error("Use 1-24 letters, numbers, underscores, or hyphens."));
            return;
        }

        string error = null;
        WebSocket previous = null;
        bool alreadyActive = false;
        lock (Sync)
        {
            if (Ids.ContainsKey(socket))
            {
                error = "You are already in the room.";
            }
            else if (Members.TryGetValue(userId, out previous))
            {
                alreadyActive = true;
                Ids.Remove(previous);
                Members[userId] = socket;
                Ids[socket] = userId;
            }
            else
            {
                Members[userId] = socket;
                Ids[socket] = userId;
            }
        }

        if (error != null)
        {
            await Send(socket, Error(error));
            return;
        }

        if (previous != null)
        {
            try { previous.Abort(); }
            catch (Exception) { }
        }

        await Send(socket, Welcome(userId));
        if (!alreadyActive)
        {
            await Broadcast(Event("join", userId), socket);
        }
    }

    static async Task Leave(WebSocket socket)
    {
        string userId = null;
        lock (Sync)
        {
            if (Ids.ContainsKey(socket))
            {
                userId = Ids[socket];
                Ids.Remove(socket);
                WebSocket current;
                if (Members.TryGetValue(userId, out current) && current == socket)
                {
                    Members.Remove(userId);
                }
            }
        }

        if (userId != null)
        {
            await Broadcast(Event("leave", userId), null);
        }
    }

    static Dictionary<string, object> Welcome(string userId)
    {
        Dictionary<string, object> payload = new Dictionary<string, object>();
        payload["type"] = "welcome";
        payload["userId"] = userId;
        lock (Sync)
        {
            payload["members"] = MemberIds();
            payload["messages"] = new List<Dictionary<string, object>>(History);
        }
        return payload;
    }

    static Dictionary<string, object> Event(string name, string userId)
    {
        Dictionary<string, object> payload = new Dictionary<string, object>();
        payload["type"] = "event";
        payload["event"] = name;
        payload["userId"] = userId;
        payload["ts"] = Now();
        lock (Sync)
        {
            payload["members"] = MemberIds();
        }
        return payload;
    }

    static List<string> MemberIds()
    {
        List<string> ids = new List<string>(Members.Keys);
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    static string CurrentId(WebSocket socket)
    {
        lock (Sync)
        {
            string userId;
            if (Ids.TryGetValue(socket, out userId)) return userId;
            return null;
        }
    }

    static Dictionary<string, object> Error(string message)
    {
        Dictionary<string, object> payload = new Dictionary<string, object>();
        payload["type"] = "error";
        payload["message"] = message;
        return payload;
    }

    static string Field(Dictionary<string, object> data, string key)
    {
        object value;
        if (!data.TryGetValue(key, out value) || value == null) return "";
        return value.ToString();
    }

    static async Task Broadcast(object payload, WebSocket except)
    {
        List<WebSocket> targets = new List<WebSocket>();
        lock (Sync)
        {
            foreach (KeyValuePair<string, WebSocket> pair in Members)
            {
                if (pair.Value != except) targets.Add(pair.Value);
            }
        }

        foreach (WebSocket target in targets)
        {
            await Send(target, payload);
        }
    }

    static async Task Sweep()
    {
        while (true)
        {
            await Task.Delay(15000);
            List<WebSocket> sockets = new List<WebSocket>();
            lock (Sync)
            {
                foreach (WebSocket socket in Members.Values) sockets.Add(socket);
            }

            Dictionary<string, object> ping = new Dictionary<string, object>();
            ping["type"] = "ping";
            foreach (WebSocket socket in sockets)
            {
                await Send(socket, ping);
            }
        }
    }

    static async Task Send(WebSocket socket, object payload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(payload));
        bool failed = false;
        await SendLock.WaitAsync();
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                failed = true;
            }
            else
            {
                CancellationTokenSource timeout = new CancellationTokenSource();
                timeout.CancelAfter(3000);
                try
                {
                    await socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        timeout.Token
                    );
                }
                catch (Exception)
                {
                    failed = true;
                }
            }
        }
        finally
        {
            SendLock.Release();
        }

        if (failed)
        {
            try { socket.Abort(); }
            catch (Exception) { }
            await Leave(socket);
        }
    }

    static long Now()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }
}
