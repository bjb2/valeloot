using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ValeLoot;

/// <summary>
/// The rule editor, served to the player's own browser from inside the game process.
///
/// Press F8. The default browser opens on `http://127.0.0.1:38512/`, which is ValeLoot's own editor
/// page with the real bag, the real rules and the real item catalog already in it. Edit, save, and the
/// bag recolours on the next redraw. That is the whole feature, and it replaces six steps of friction:
/// find an HTML file in a plugins folder, pick a browser that has the right API, grant a folder in a
/// native dialog, and hope the two generated text files beside it were current.
///
/// ## What this listener is, said plainly, because it is a socket
///
/// It binds **127.0.0.1 only** — never `0.0.0.0`, never a LAN interface — so it is reachable from this
/// machine and from nothing else. It answers exactly four routes: its own embedded editor page, a JSON
/// snapshot of the player's own rules/bag/catalog, a save endpoint that writes the player's own rule
/// file, and a one-field health probe. It carries **no game traffic**, hooks nothing on the game's
/// network path, and contains **no packet capture** — there is no code here that could observe a game
/// packet. The maintainer's audit greps for the game's transport library, its manager type and its raw
/// send calls; those terms are deliberately not spelled anywhere in this folder, including in this
/// comment, so that the audit's only possible hit would be real code. Nothing leaves the machine:
/// there is no outbound request anywhere in this file.
///
/// An earlier version of this mod deleted all socket code and the README claimed "no networking at
/// all". That claim is now FALSE and has been rewritten rather than softened, in both READMEs, in the
/// boot log, and on the page itself. A disclosure a player has to infer is not a disclosure.
///
/// `Editor/Enabled = false` in `com.savi.valeloot.cfg` turns it off completely. The editor is a
/// convenience; highlighting is the product. If the port is busy the log says so in one plain line,
/// every other feature keeps working, and F8 falls back to opening the `file://` copy on disk.
///
/// ## Why the game state is a SNAPSHOT and never a live read
///
/// **Touching il2cpp objects off the main thread is how this project crashed the game once already**
/// (see `Il2CppMeta.FindOverload` — a wrong overload dereferenced a `true` as an object reference and
/// took the process down with an `AccessViolationException` inside a hook). The listener thread
/// therefore never calls into il2cpp, never dereferences an `IntPtr`, and never walks the inventory.
///
/// Everything it serves is captured on the MAIN thread, where the paint pass already gathered it for
/// `valeloot-bag.txt` and `ItemCatalog`, and handed over as an immutable object graph that is swapped
/// in by one reference assignment. The HTTP thread reads that one reference and serialises plain
/// strings and ints. The only files it touches are the player's rule file and its own embedded page.
///
/// ## Threading, in one paragraph
///
/// One background thread owns `HttpListener` and serves requests serially — a 120 KB page over
/// loopback for one browser needs no thread pool, and serial means there is no shared mutable state
/// to protect. `Pump()` runs on Unity's main thread from a `PlayerSave.Update` detour (the KB's
/// finding: a Unity message is the only dispatch point that can be neither inlined away nor routed
/// around) and is the single writer of <see cref="_snapshot"/>. Single writer plus immutable payload
/// means no lock anywhere in this file.
/// </summary>
internal static class EditorServer
{
    public const int DefaultPort = 38512;
    public const string DefaultHotkey = "F8";

    /// <summary>The editor page, compiled into the DLL. `LogicalName` in ValeLoot.csproj.</summary>
    private const string ResourceName = "ValeLoot.editor.html";

    /// <summary>The `file://` fallback copy, written beside the config file.</summary>
    public const string FallbackFileName = "ValeLoot-editor.html";

    /// <summary>
    /// A runaway client must not be able to grow this process. A filter file is a few KB; a quarter
    /// of a megabyte is four decimal orders of headroom and still nothing.
    /// </summary>
    private const int MaxPostBytes = 256 * 1024;

    /// <summary>
    /// Two seconds of "no, you already asked". A key held for three frames is one browser, and
    /// `ShellExecute` is slow enough that a second one lands before the first window is up.
    /// </summary>
    private const long OpenDebounceMs = 2000;

    /// <summary>Serving. False when disabled, when the page is missing, or when the port was busy.</summary>
    public static bool Serving { get; private set; }

    /// <summary>What F8 opens: the served URL when serving, otherwise the `file://` fallback.</summary>
    public static string Url { get; private set; } = "";

    /// <summary>One line for the boot log and the census, in the shape the other modules use.</summary>
    public static string Status { get; private set; } = "not installed";

    public static long Requests;
    public static long Saves;

    private static Action<string> _log = _ => { };
    private static int _port = DefaultPort;
    private static string _fallbackPath = "";
    private static byte[] _page = Array.Empty<byte>();

    private static HttpListener? _listener;
    private static Thread? _thread;
    private static volatile bool _stopping;

    /// <summary>
    /// Everything the editor is served, as one immutable object. Written only by <see cref="Tick"/>
    /// on the main thread and read only by the listener thread, so the reference swap is the whole
    /// of the synchronisation: a reader sees either the previous snapshot entire or the next one.
    /// </summary>
    private static volatile Snapshot _snapshot = Snapshot.Empty;

    // ---- the main-thread tick, and the hotkey it reads --------------------------------------------

    private delegate void UpdateFn(IntPtr self);

    /// <summary>
    /// `Input.GetKeyDown(KeyCode)`. Static il2cpp methods take (args..., MethodInfo*) and no `this`;
    /// the return is a managed bool, which is one byte on the wire, so it is taken as a byte rather
    /// than letting the marshaller pick the 4-byte Win32 BOOL.
    /// </summary>
    private delegate byte GetKeyDownFn(int keyCode, IntPtr methodInfo);

    private static object? _updateDetour;
    private static UpdateFn? _updateHook;
    private static UpdateFn? _updateOriginal;
    private static GetKeyDownFn? _getKeyDown;

    private static int _hotkeyCode = -1;
    private static long _lastOpen;

    /// <summary>The per-frame detour applied. Without it there is no live bag and no hotkey.</summary>
    public static bool Ticking { get; private set; }

    /// <summary>Set after the first exception out of the tick, so a broken frame is not a log flood.</summary>
    private static bool _tickFailed;

    private static int _catalogGeneration = -1;

    /**
     * Bring the editor up: page, fallback copy, listener, tick. Never fatal.
     *
     * Order matters. The embedded page and the `file://` fallback come first and unconditionally,
     * because they are what a player falls back TO — a port that will not bind, or an editor switched
     * off, must still leave a working way to edit rules. The listener is next, and its failure is one
     * log line. The tick is last, because it needs a URL to name in its own log line.
     */
    public static void Install(string configDirectory, bool enabled, int port, string hotkey, Action<string> log)
    {
        _log = log;
        // Cleared before the serve thread can read it, so a re-Install after Uninstall serves again.
        _stopping = false;
        _port = port is >= 1 and <= 65535 ? port : DefaultPort;
        if (_port != port)
        {
            log($"editor Port {port} is not a port number; using {_port}.");
        }

        _page = LoadPage();
        if (_page.Length == 0)
        {
            // The page is an EmbeddedResource and the build fails without it, so this means the DLL
            // on disk is damaged. Serving nothing at `/` would be worse than not serving.
            Status = $"editor page resource {ResourceName} missing from the assembly";
            log($"editor NOT available: {Status}. Reinstall the plugin; highlighting is unaffected.");
            return;
        }

        WriteFallback(configDirectory);

        if (enabled) StartListening();
        else
        {
            Status = "off (Editor/Enabled = false)";
            log($"editor server off (Enabled = false under [Editor]). No port is opened. F8 opens "
              + $"{_fallbackPath} directly instead, which edits your filter file the same way.");
        }

        Url = Serving ? BaseUrl : FileUrl(_fallbackPath);
        BindTick(hotkey);
    }

    private static string BaseUrl => $"http://127.0.0.1:{_port.ToString(CultureInfo.InvariantCulture)}/";

    public static void Uninstall()
    {
        _stopping = true;
        Detours.Undo(ref _updateDetour);
        _updateHook = null;
        _updateOriginal = null;
        _getKeyDown = null;
        Ticking = false;
        _tickFailed = false;
        _hotkeyCode = -1;

        // Stop() unblocks the thread parked in GetContext by throwing into it, which the loop treats
        // as "we are done". Close() releases the port.
        try { _listener?.Stop(); } catch { /* teardown must never throw */ }
        try { _listener?.Close(); } catch { /* teardown must never throw */ }
        _listener = null;

        try { _thread?.Join(500); } catch { /* teardown must never throw */ }
        _thread = null;

        Serving = false;
        _snapshot = Snapshot.Empty;
        _catalogGeneration = -1;
    }

    // ---- boot: the page, the fallback, the port ---------------------------------------------------

    /// <summary>The editor page out of the assembly. Empty means the resource is not there.</summary>
    private static byte[] LoadPage()
    {
        try
        {
            Assembly assembly = typeof(EditorServer).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return Array.Empty<byte>();
            var buffer = new MemoryStream(stream.CanSeek ? (int)stream.Length : 128 * 1024);
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception e)
        {
            _log($"editor page could not be read out of the plugin — {e.Message}");
            return Array.Empty<byte>();
        }
    }

    /**
     * The `file://` fallback: the same page, on disk, next to the config file.
     *
     * It exists for the player whose port is blocked and for the player who wants to edit a filter
     * with the game shut, and the shipped zip deliberately contains no HTML for anyone to find — this
     * copy IS the file mode. Written when it is missing or when its bytes differ from the embedded
     * page, so an upgrade refreshes a stale copy and an unchanged boot touches no disk.
     */
    private static void WriteFallback(string configDirectory)
    {
        _fallbackPath = Path.Combine(configDirectory, FallbackFileName);
        try
        {
            if (File.Exists(_fallbackPath) && Same(File.ReadAllBytes(_fallbackPath), _page)) return;

            Directory.CreateDirectory(configDirectory);
            string temp = _fallbackPath + ".tmp";
            File.WriteAllBytes(temp, _page);
            File.Move(temp, _fallbackPath, overwrite: true);
            _log($"editor fallback page written to {_fallbackPath} — open that file directly if the "
               + "server is off or its port is busy. It edits the same filter file.");
        }
        catch (Exception e)
        {
            _log($"could not write {_fallbackPath} — {e.Message}. The served editor is unaffected.");
        }
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /**
     * Bind 127.0.0.1 and start serving.
     *
     * `127.0.0.1` is spelled out rather than `localhost` or `+`: `http://+:port/` is every interface
     * on the machine, which is precisely the thing this must never be, and a literal loopback address
     * cannot be pointed elsewhere by a hosts file.
     *
     * A busy port is an ordinary outcome, not an error to survive grudgingly — a second copy of the
     * game, or anything else that took 38512. It gets one plain line naming the port and the three
     * ways out, and every other feature carries on untouched.
     */
    private static void StartListening()
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(BaseUrl);
            listener.Start();
            _listener = listener;

            _thread = new Thread(Serve)
            {
                IsBackground = true,        // never keep the game's process alive at shutdown
                Name = "ValeLoot editor",
            };
            _thread.Start();

            Serving = true;
            Status = $"serving {BaseUrl} (127.0.0.1 only)";
            _log($"editor server on {BaseUrl} — bound to 127.0.0.1 ONLY, so it is reachable from this "
               + "machine and from nothing else. It serves ValeLoot's own editor page and your own rule "
               + "file, carries no game traffic, contains no packet capture, and sends nothing anywhere. "
               + "Turn it off with Enabled = false under [Editor] in com.savi.valeloot.cfg.");
        }
        catch (Exception e)
        {
            Serving = false;
            _listener = null;
            Status = $"port {_port.ToString(CultureInfo.InvariantCulture)} unavailable";
            _log($"editor server could NOT bind 127.0.0.1:{_port.ToString(CultureInfo.InvariantCulture)} "
               + $"— {e.Message}. Highlighting, sounds, hot reload and the hover note are all unaffected: "
               + "the editor is a convenience. Free that port, set a different Port under [Editor] in "
               + $"com.savi.valeloot.cfg, or just open {_fallbackPath} directly — F8 will now do that for you.");
        }
    }

    // ---- the listener thread ---------------------------------------------------------------------

    /**
     * One request at a time, forever, on this thread and no other.
     *
     * Serial on purpose: the only client is one browser on loopback asking for a page and a JSON
     * blob, and serving them in order means there is no concurrent access to anything — including to
     * <see cref="_snapshot"/>, which is a single volatile read per request either way.
     *
     * Nothing here may throw out of the loop. A malformed request, a browser that hung up mid-body,
     * a filter file someone deleted — each is one 4xx/5xx and the next request.
     */
    private static void Serve()
    {
        HttpListener? listener = _listener;
        if (listener is null) return;

        while (!_stopping)
        {
            HttpListenerContext context;
            try
            {
                context = listener.GetContext();
            }
            catch (HttpListenerException)
            {
                return;             // Stop() threw into us: shutting down
            }
            catch (ObjectDisposedException)
            {
                return;             // Close() beat us to it
            }
            catch (InvalidOperationException)
            {
                return;             // listener no longer started
            }

            Requests++;
            try
            {
                Handle(context);
            }
            catch (Exception e)
            {
                TrySend(context, 500, "application/json", Fail($"the editor server failed to answer: {e.Message}"));
            }
            finally
            {
                try { context.Response.Close(); } catch { /* the client hung up; nothing to do */ }
            }
        }
    }

    /**
     * Route one request, after establishing that it came from this machine and this page.
     *
     * Three cheap refusals before any route runs, in order of how badly they would matter:
     *
     * 1. **Remote endpoint must be loopback.** The bind already guarantees it; this is the assertion
     *    that keeps it true if the prefix is ever edited by someone who does not read the comment.
     * 2. **`Host` must be 127.0.0.1 or localhost.** A page on the open internet can point a DNS name
     *    at 127.0.0.1 and have the player's own browser talk to this server (DNS rebinding). The
     *    `Host` header is the one field that attack cannot forge past.
     * 3. **`Origin`, when present, must be ours.** No `Access-Control-Allow-Origin` is ever sent, and
     *    a foreign origin is refused outright rather than left to the browser to enforce — otherwise
     *    any tab the player has open could POST a new filter over theirs.
     *
     * Then: exactly four routes, and 404 for everything else. There is no directory serving here and
     * no path is ever joined to a filesystem root — the only file this can emit is its own embedded
     * page, so there is no traversal to get wrong.
     */
    private static void Handle(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;

        if (request.RemoteEndPoint is null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address))
        {
            TrySend(context, 403, "application/json",
                    Fail("ValeLoot's editor answers only the machine it runs on."));
            return;
        }

        if (!HostIsLoopback(request.UserHostName))
        {
            TrySend(context, 403, "application/json",
                    Fail("unexpected Host header; ValeLoot's editor answers only 127.0.0.1 and localhost."));
            return;
        }

        string? origin = request.Headers["Origin"];
        if (origin is not null && !OriginIsOurs(origin))
        {
            TrySend(context, 403, "application/json",
                    Fail("ValeLoot's editor does not answer other web pages."));
            return;
        }

        string route = request.Url?.AbsolutePath ?? "/";
        string method = request.HttpMethod;

        switch (route)
        {
            case "":
            case "/":
                if (method != "GET" && method != "HEAD") { MethodNotAllowed(context, "GET"); return; }
                Send(context, 200, "text/html; charset=utf-8", _page, method == "HEAD");
                return;

            case "/api/health":
                if (method != "GET") { MethodNotAllowed(context, "GET"); return; }
                Send(context, 200, "application/json", Utf8("{\"ok\":true}"));
                return;

            case "/api/state":
                if (method != "GET") { MethodNotAllowed(context, "GET"); return; }
                SendState(context);
                return;

            /**
             * One `.wav` out of the sounds directory, so the editor's ▶ plays the REAL file.
             *
             * The page used to synthesise the five built-ins in WebAudio and tell you it could not
             * reach anything else — which meant the moment a player used their own file, the one
             * button whose entire job is "let me hear it" stopped working. It can reach it now,
             * through the mod that is already serving the page.
             *
             * `name` goes through `LootSound.TryResolve`, the same call `Play` uses, so this route
             * can serve exactly the set of files a filter could name and not one byte more. There is
             * no path here to join and no extension to choose: a name is letters, digits, dot, dash
             * and underscore, must start alphanumeric, and `.wav` is appended by the resolver.
             */
            case "/api/sound":
            {
                if (method != "GET" && method != "HEAD") { MethodNotAllowed(context, "GET"); return; }
                string wanted = request.QueryString["name"] ?? "";
                if (!LootSound.TryResolve(wanted, out string soundPath))
                {
                    TrySend(context, 404, "application/json",
                            Fail("no such sound in the valeloot-sounds folder."));
                    return;
                }
                byte[] audio;
                try
                {
                    audio = File.ReadAllBytes(soundPath);
                }
                catch (Exception e)
                {
                    // A file being rewritten as the page asks for it. Says so rather than 500-ing:
                    // the audition is a convenience and the mod will still play it at pickup time.
                    TrySend(context, 503, "application/json", Fail($"could not read that sound — {e.Message}"));
                    return;
                }
                Send(context, 200, "audio/wav", audio, method == "HEAD");
                return;
            }

            case "/api/filter":
                if (method != "POST") { MethodNotAllowed(context, "POST"); return; }
                SaveFilter(context);
                return;

            default:
                TrySend(context, 404, "application/json",
                        Fail($"ValeLoot's editor serves /, /api/state, /api/filter, /api/sound and /api/health. No {route}."));
                return;
        }
    }

    private static void MethodNotAllowed(HttpListenerContext context, string allowed)
    {
        try { context.Response.AddHeader("Allow", allowed); } catch { /* client hung up */ }
        TrySend(context, 405, "application/json", Fail($"that route takes {allowed}."));
    }

    /// <summary>Host header host part is loopback. Port is not checked: a wrong one cannot reach us.</summary>
    private static bool HostIsLoopback(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;

        // Strip the port. `[::1]:38512` keeps its brackets, so the bracketed form is matched whole.
        string name = host;
        int colon = name.LastIndexOf(':');
        if (colon > 0 && name.IndexOf(']') < colon) name = name.Substring(0, colon);

        return name.Equals("127.0.0.1", StringComparison.Ordinal)
            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("[::1]", StringComparison.Ordinal)
            || name.Equals("::1", StringComparison.Ordinal);
    }

    /// <summary>Origin is this exact server. Anything else — including `null` from a file:// page.</summary>
    private static bool OriginIsOurs(string origin)
    {
        string port = _port.ToString(CultureInfo.InvariantCulture);
        return origin.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }

    // ---- GET /api/state --------------------------------------------------------------------------

    /**
     * The whole editor payload: the mod, the player's filter text, the catalog, the bag.
     *
     * The filter is read from DISK here rather than served from the parsed rule list, because the
     * player edits that file by hand too and the editor must never show them a stale copy of their
     * own work. If it cannot be read, this answers 503 and no text at all — an editor that shows an
     * empty filter next to a live Save button is one click away from deleting someone's rules.
     */
    private static void SendState(HttpListenerContext context)
    {
        string path = FilterFile.Path;
        string filter;
        try
        {
            filter = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            // Commonest cause by far: caught inside another writer's replace window. The page retries;
            // it must not be handed a blank filter it could then save over the real one.
            TrySend(context, 503, "application/json",
                    Fail($"could not read {path} — {e.Message}"));
            return;
        }

        Snapshot snap = _snapshot;      // one read; everything reachable from it is immutable

        // Sized for a full session: ~800 catalog entries and ~200 bag rows land near 200 KB, and one
        // grow of a 96 KB builder is cheaper than the copies a default-sized one would do.
        var json = new StringBuilder(96 * 1024);
        json.Append("{\"mod\":{\"version\":");
        Str(json, Plugin.PluginVersion);
        json.Append(",\"port\":").Append(_port.ToString(CultureInfo.InvariantCulture))
            .Append(",\"served\":").Append(Serving ? "true" : "false")
            .Append("},\"filter\":");
        Str(json, filter);
        json.Append(",\"filterPath\":");
        Str(json, path);
        json.Append(",\"threshold\":").Append(snap.Threshold.ToString(CultureInfo.InvariantCulture));

        json.Append(",\"catalog\":{\"ready\":").Append(snap.CatalogReady ? "true" : "false")
            .Append(",\"items\":").Append(snap.Items.Length.ToString(CultureInfo.InvariantCulture))
            .Append('}');

        /**
         * The pickup watcher's own counters, so "why did I not hear a sound?" is answerable without a
         * restart.
         *
         * They were previously logged once at boot, which is the least useful moment: at boot there is no
         * character, so the line always reads "waiting for a character, 0 checks" and says nothing about
         * whether the per-frame tick is alive minutes later. `checks` climbing proves the tick fires at
         * all — the single fact that separates "no qualifying item has dropped" from "the hook never
         * runs", which are the same silence to a player.
         */
        json.Append(",\"pickups\":{\"installed\":").Append(InventoryWatch.Installed ? "true" : "false")
            .Append(",\"primed\":").Append(InventoryWatch.Primed ? "true" : "false")
            .Append(",\"held\":").Append(InventoryWatch.Held.ToString(CultureInfo.InvariantCulture))
            .Append(",\"checks\":").Append(InventoryWatch.Checks.ToString(CultureInfo.InvariantCulture))
            .Append(",\"walks\":").Append(InventoryWatch.Walks.ToString(CultureInfo.InvariantCulture))
            // `primes` sits next to `_primed = true` in the walk, so it separates the two ways a
            // watcher can look dead: primes climbing with `primed` false means something CLEARS the
            // baseline after every walk, primes stuck at zero means the walk never finishes.
            .Append(",\"primes\":").Append(InventoryWatch.Primes.ToString(CultureInfo.InvariantCulture))
            .Append(",\"forgetNoChar\":").Append(InventoryWatch.ForgetNoCharacter.ToString(CultureInfo.InvariantCulture))
            .Append(",\"forgetSwitch\":").Append(InventoryWatch.ForgetSwitch.ToString(CultureInfo.InvariantCulture))
            .Append(",\"forgetMissing\":").Append(InventoryWatch.ForgetMissingBag.ToString(CultureInfo.InvariantCulture))
            .Append(",\"forgetShape\":").Append(InventoryWatch.ForgetShapeChange.ToString(CultureInfo.InvariantCulture))
            .Append(",\"strangers\":").Append(InventoryWatch.Strangers.ToString(CultureInfo.InvariantCulture))
            .Append(",\"arrivals\":").Append(InventoryWatch.Arrivals.ToString(CultureInfo.InvariantCulture))
            .Append(",\"announced\":").Append(LootSound.Announced.ToString(CultureInfo.InvariantCulture))
            .Append(",\"played\":").Append(LootSound.Played.ToString(CultureInfo.InvariantCulture))
            .Append(",\"suppressed\":").Append(LootSound.Suppressed.ToString(CultureInfo.InvariantCulture))
            .Append(",\"soundOn\":").Append(LootSound.Enabled ? "true" : "false")
            .Append(",\"lastUid\":");
        Str(json, LootSound.LastUid ?? "");
        json.Append('}');

        /**
         * The sounds actually on disk, so the editor offers a LIST instead of a text field.
         *
         * Read here rather than captured in the main-thread snapshot on purpose: it is a directory
         * listing, touching no il2cpp, and it has to reflect a `.wav` the player dropped in a moment
         * ago rather than whatever was there when the last frame ticked. `LootSound.Names` throttles
         * the scan, so a page polling this does not stat the folder on every request.
         */
        json.Append(",\"sounds\":[");
        string[] sounds = LootSound.Names();
        for (int i = 0; i < sounds.Length; i++)
        {
            if (i > 0) json.Append(',');
            Str(json, sounds[i]);
        }
        json.Append(']');

        json.Append(",\"items\":[");
        for (int i = 0; i < snap.Items.Length; i++)
        {
            if (i > 0) json.Append(',');
            CatalogItem item = snap.Items[i];
            json.Append("{\"id\":");
            Str(json, item.Id);
            json.Append(",\"name\":");
            Str(json, item.Name);
            json.Append(",\"type\":");
            Str(json, item.Type);
            json.Append(",\"level\":").Append(item.Level.ToString(CultureInfo.InvariantCulture))
                .Append(",\"set\":");
            Str(json, item.Set);
            json.Append('}');
        }
        json.Append(']');

        json.Append(",\"stats\":[");
        for (int i = 0; i < snap.Stats.Length; i++)
        {
            if (i > 0) json.Append(',');
            Str(json, snap.Stats[i]);
        }
        json.Append(']');

        json.Append(",\"bag\":[");
        for (int i = 0; i < snap.Bag.Length; i++)
        {
            if (i > 0) json.Append(',');
            snap.Bag[i].Write(json, snap.Threshold);
        }
        json.Append(']');

        json.Append(",\"bagCoverage\":");
        Str(json, snap.Coverage);
        json.Append(",\"generated\":");
        Str(json, snap.Generated);
        json.Append('}');

        Send(context, 200, "application/json", Utf8(json.ToString()));
    }

    // ---- POST /api/filter ------------------------------------------------------------------------

    /**
     * Write the player's rule file, and let the existing watcher do the rest.
     *
     * Temp file then replace, so the watcher can never pick up half a filter: `FilterFile` reloads on
     * a change event and a truncated read would either drop every rule below the cut or report a wall
     * of parse errors for a file the player never wrote. `File.Move(overwrite: true)` is a single
     * `MoveFileEx` with `MOVEFILE_REPLACE_EXISTING` on the same volume — the same guarantee
     * `BagSnapshot` relies on for the file this editor reads.
     *
     * Nothing is reloaded from here. The watcher sets a flag and the paint pass re-parses on the main
     * thread, which is both the correct thread and the exact moment the result becomes visible.
     */
    private static void SaveFilter(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;

        if (request.ContentLength64 > MaxPostBytes)
        {
            TrySend(context, 413, "application/json",
                    Fail($"that filter is {request.ContentLength64.ToString(CultureInfo.InvariantCulture)} bytes; "
                       + $"ValeLoot accepts up to {MaxPostBytes.ToString(CultureInfo.InvariantCulture)}."));
            return;
        }

        byte[] body;
        try
        {
            body = ReadCapped(request.InputStream);
        }
        catch (InvalidDataException)
        {
            TrySend(context, 413, "application/json",
                    Fail($"that filter is over ValeLoot's {MaxPostBytes.ToString(CultureInfo.InvariantCulture)}-byte limit."));
            return;
        }
        catch (Exception e)
        {
            TrySend(context, 400, "application/json", Fail($"could not read the request body — {e.Message}"));
            return;
        }

        string path = FilterFile.Path;
        if (path.Length == 0)
        {
            TrySend(context, 503, "application/json",
                    Fail("ValeLoot has no filter file this session, so there is nowhere to save. See the BepInEx log."));
            return;
        }

        // The browser may prepend a BOM. Strip it: the file this mod writes has never had one, and a
        // BOM on line 1 would make the first `Threshold` an unparsable token.
        int start = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? 3 : 0;

        string temp = path + ".editor.tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(body, start, body.Length - start);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            // The real OS reason, verbatim, because the page shows this string to the player. "false"
            // on its own sends them to a forum; "Access to the path ... is denied" sends them to the
            // file's properties dialog.
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            TrySend(context, 500, "application/json", Fail($"could not write {path} — {e.Message}"));
            return;
        }

        Saves++;
        int written = body.Length - start;
        Send(context, 200, "application/json",
             Utf8("{\"ok\":true,\"bytes\":" + written.ToString(CultureInfo.InvariantCulture) + "}"));
    }

    /// <summary>Read a request body, refusing at the cap rather than after it.</summary>
    private static byte[] ReadCapped(Stream input)
    {
        var buffer = new MemoryStream(8 * 1024);
        var chunk = new byte[8 * 1024];
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxPostBytes) throw new InvalidDataException("body over cap");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // ---- responses -------------------------------------------------------------------------------

    /**
     * Send one response, and never cache.
     *
     * `no-store` is not belt-and-braces here: the page is compiled into the DLL, so an upgrade
     * changes the bytes at `/` while the URL stays identical. A browser that revalidated on its own
     * schedule would serve the old editor against the new mod's JSON, which is the one failure mode
     * that looks like the mod being broken rather than the cache being stale.
     */
    private static void Send(HttpListenerContext context, int status, string contentType, byte[] body,
                             bool headersOnly = false)
    {
        HttpListenerResponse response = context.Response;
        response.StatusCode = status;
        response.ContentType = contentType;
        response.AddHeader("Cache-Control", "no-store, no-cache, must-revalidate");
        response.AddHeader("Pragma", "no-cache");
        // Nothing here is meant to be framed or sniffed by another page.
        response.AddHeader("X-Content-Type-Options", "nosniff");
        response.AddHeader("Referrer-Policy", "no-referrer");
        response.ContentLength64 = headersOnly ? 0 : body.Length;
        if (!headersOnly) response.OutputStream.Write(body, 0, body.Length);
    }

    /// <summary>Send that tolerates a client which has already gone — used on every error path.</summary>
    private static void TrySend(HttpListenerContext context, int status, string contentType, byte[] body)
    {
        try { Send(context, status, contentType, body); } catch { /* the client hung up */ }
    }

    private static byte[] Fail(string reason)
    {
        var json = new StringBuilder(reason.Length + 32);
        json.Append("{\"ok\":false,\"error\":");
        Str(json, reason);
        json.Append('}');
        return Utf8(json.ToString());
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    // ---- JSON, by hand ---------------------------------------------------------------------------

    /**
     * A JSON string, escaped properly. This is not a formality.
     *
     * The game's own UI text contains raw control characters, and this project has already lost a
     * debugging round to a hand-built report that a JSON parser silently rejected while the sender
     * cheerfully logged that it had sent it. So: quote and backslash escaped, EVERY character below
     * 0x20 escaped (the five with short forms by name, the rest as `\u00xx`), and any unpaired
     * surrogate replaced with U+FFFD rather than emitted — see the comment at that branch, which is
     * the second half of the same lesson.
     *
     * Anything else is passed through and encoded as UTF-8, which is what the response declares.
     */
    private static void Str(StringBuilder json, string value)
    {
        json.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': json.Append("\\\""); continue;
                case '\\': json.Append("\\\\"); continue;
                case '\b': json.Append("\\b"); continue;
                case '\f': json.Append("\\f"); continue;
                case '\n': json.Append("\\n"); continue;
                case '\r': json.Append("\\r"); continue;
                case '\t': json.Append("\\t"); continue;
            }

            if (c < ' ')
            {
                Escape(json, c);
                continue;
            }

            /*
             * An unpaired surrogate is half a character and carries no information. It is replaced
             * with U+FFFD here, DELIBERATELY and in one place, rather than left to be replaced by
             * `Encoding.UTF8` on the way out.
             *
             * Escaping it as `\ud800` was the first choice and it is wrong: that is legal JSON
             * grammar and a browser's `JSON.parse` accepts it, but a strict reader rejects the whole
             * document — `System.Text.Json` throws "Cannot read incomplete UTF-16 JSON text as string
             * with missing low surrogate". Emitting a payload that some parsers refuse is precisely
             * the failure this method exists to prevent, and one visible replacement character in a
             * display name is a far better outcome than a state request that cannot be read at all.
             */
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    json.Append(c).Append(value[i + 1]);
                    i++;
                }
                else json.Append('\uFFFD');
                continue;
            }

            if (char.IsLowSurrogate(c)) json.Append('\uFFFD');
            else json.Append(c);
        }
        json.Append('"');
    }

    private static void Escape(StringBuilder json, char c)
        => json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));

    // ---- the snapshot ----------------------------------------------------------------------------

    /// <summary>One substat line, as the editor reads it.</summary>
    internal sealed class BagLine
    {
        public readonly string Stat;
        public readonly int Roll;
        /// <summary>What the game prints, or -1 where the catalog could not say. Serialises as null.</summary>
        public readonly int Printed;

        public BagLine(string stat, int roll, int printed)
        {
            Stat = stat;
            Roll = roll;
            Printed = printed;
        }
    }

    /**
     * One item in the bag, captured on the main thread and never touched again.
     *
     * `TopRolls` and `AvgRoll` are NOT stored: they are functions of the rolls and the threshold, and
     * the threshold changes the moment the player saves `Threshold 95` — storing them would hand the
     * editor counts from the previous filter. They are computed at serialise time, on the listener
     * thread, from the immutable roll list.
     */
    internal sealed class BagItem
    {
        public readonly string Uid;
        public readonly string ItemId;
        public readonly string Name;
        public readonly string Type;
        public readonly int Refine;
        public readonly bool Favorite;
        public readonly BagLine[] Lines;

        public BagItem(string uid, string itemId, string name, string type, int refine, bool favorite,
                       BagLine[] lines)
        {
            Uid = uid;
            ItemId = itemId;
            Name = name;
            Type = type;
            Refine = refine;
            Favorite = favorite;
            Lines = lines;
        }

        public void Write(StringBuilder json, int threshold)
        {
            json.Append("{\"uid\":");
            Str(json, Uid);
            json.Append(",\"itemId\":");
            Str(json, ItemId);
            json.Append(",\"name\":");
            Str(json, Name);
            json.Append(",\"type\":");
            Str(json, Type);
            json.Append(",\"refine\":").Append(Refine.ToString(CultureInfo.InvariantCulture))
                .Append(",\"favorite\":").Append(Favorite ? "true" : "false");

            /*
             * The same two answers `LootFilter.ItemFacts.TopRolls` and `AverageRoll` give, by the same
             * rules — at or above the threshold counts, the average is rounded away from zero and then
             * compared as a whole number, and an item with no lines has NO average rather than zero.
             * They cannot call those: `ItemFacts` is one buffer refilled per cell on the main thread,
             * and this runs on the listener thread against a snapshot. Keep the two in step; a filter
             * whose preview disagrees with the game is worse than a preview that says nothing.
             */
            int top = 0;
            int sum = 0;
            for (int i = 0; i < Lines.Length; i++)
            {
                if (Lines[i].Roll >= threshold) top++;
                sum += Lines[i].Roll;
            }
            json.Append(",\"topRolls\":").Append(top.ToString(CultureInfo.InvariantCulture))
                .Append(",\"avgRoll\":");
            if (Lines.Length == 0) json.Append("null");
            else
            {
                json.Append(((int)Math.Round(sum / (double)Lines.Length, MidpointRounding.AwayFromZero))
                            .ToString(CultureInfo.InvariantCulture));
            }

            json.Append(",\"lines\":[");
            for (int i = 0; i < Lines.Length; i++)
            {
                if (i > 0) json.Append(',');
                BagLine line = Lines[i];
                json.Append("{\"stat\":");
                Str(json, line.Stat);
                json.Append(",\"roll\":").Append(line.Roll.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"printed\":");
                // null, never 0: "the catalog could not say" must fail every value bound, and a 0
                // would satisfy `Stat Agi >= 0` and read as a real measurement of a bad line.
                if (line.Printed < 0) json.Append("null");
                else json.Append(line.Printed.ToString(CultureInfo.InvariantCulture));
                json.Append('}');
            }
            json.Append("]}");
        }
    }

    /// <summary>One equip from the game's own catalog — the editor's autocomplete and type vocabulary.</summary>
    internal sealed class CatalogItem
    {
        public readonly string Id;
        public readonly string Name;
        public readonly string Type;
        public readonly int Level;
        public readonly string Set;

        public CatalogItem(string id, string name, string type, int level, string set)
        {
            Id = id;
            Name = name;
            Type = type;
            Level = level;
            Set = set;
        }
    }

    /// <summary>Everything served, in one object so bag, catalog and threshold cannot disagree.</summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(
            Array.Empty<BagItem>(), Array.Empty<CatalogItem>(), Array.Empty<string>(),
            LootFilter.DefaultThreshold, false,
            "nothing yet — log in and open your bag, and this fills in as you scroll.", "");

        public readonly BagItem[] Bag;
        public readonly CatalogItem[] Items;
        public readonly string[] Stats;
        public readonly int Threshold;
        public readonly bool CatalogReady;
        public readonly string Coverage;
        public readonly string Generated;

        public Snapshot(BagItem[] bag, CatalogItem[] items, string[] stats, int threshold,
                        bool catalogReady, string coverage, string generated)
        {
            Bag = bag;
            Items = items;
            Stats = stats;
            Threshold = threshold;
            CatalogReady = catalogReady;
            Coverage = coverage;
            Generated = generated;
        }

        public Snapshot WithBag(BagItem[] bag, int threshold, string coverage, string generated)
            => new(bag, Items, Stats, threshold, CatalogReady, coverage, generated);

        public Snapshot WithCatalog(CatalogItem[] items, string[] stats, bool ready)
            => new(Bag, items, stats, Threshold, ready, Coverage, Generated);
    }

    /**
     * Publish the bag. MAIN THREAD ONLY — called by `BagSnapshot`, which owns the accumulation.
     *
     * The rows arrive already projected into immutable objects by their owner, because `BagSnapshot`
     * captures them from the paint pass and this must not be a second walk of the inventory: there
     * would then be two readings of the same bag to disagree with each other, and one of them would
     * be happening on the wrong thread.
     */
    public static void PublishBag(BagItem[] bag, int threshold, bool truncated)
    {
        string coverage = truncated
            ? "your bag as the filter sees it — and it stopped counting at its cap, so this is SHORT "
            + "of your bag."
            : "your bag as the filter sees it. Items you sell, bank or dismantle drop out of this "
            + "within a second, whether the panel is open or not. The game binds one page of cells "
            + "at a time, so scroll or switch tabs to fill in items not seen yet this session.";

        _snapshot = _snapshot.WithBag(
            bag, threshold, coverage,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    /**
     * Publish the item catalog, once, when the game has loaded its configs.
     *
     * MAIN THREAD ONLY. Every field read here is a managed string or int that `ItemCatalog` extracted
     * out of il2cpp at resolve time — no `IntPtr` is dereferenced and `Entry.Config` is not touched —
     * but the dictionary behind `ItemCatalog.All` is filled on the main thread, so enumerating it
     * anywhere else would be a plain managed race on top of everything else.
     *
     * Keyed on the equip count, the same generation signal `BagSnapshot` uses: it goes from 0 to N
     * when the client finishes loading, and changes again after a content patch.
     */
    private static void PublishCatalogIfChanged()
    {
        int generation = ItemCatalog.Ready ? ItemCatalog.Count : 0;
        if (generation == _catalogGeneration) return;
        _catalogGeneration = generation;

        if (generation == 0)
        {
            _snapshot = _snapshot.WithCatalog(Array.Empty<CatalogItem>(), Array.Empty<string>(), false);
            return;
        }

        var items = new List<CatalogItem>(ItemCatalog.Count);
        foreach (ItemCatalog.Entry entry in ItemCatalog.All)
        {
            items.Add(new CatalogItem(entry.Id, entry.DisplayName, entry.TypeName, entry.LevelRequired, entry.Set));
        }

        var stats = new List<string>(ItemReader.StatNames);
        stats.Sort(StringComparer.OrdinalIgnoreCase);

        _snapshot = _snapshot.WithCatalog(items.ToArray(), stats.ToArray(), true);
    }

    // ---- the main-thread tick, and the hotkey it reads --------------------------------------------

    /**
     * Install the per-frame tick, and resolve the hotkey it reads.
     *
     * These are TWO features and the split is load-bearing. The tick is what keeps the served
     * editor's bag and item catalog current; the hotkey is a convenience on top of it. An earlier
     * shape of this method returned early when the key could not be resolved, which quietly left the
     * editor serving an empty bag and an empty catalog on any build where `Input.GetKeyDown` moved —
     * installed, green, and doing less than it says, which is the exact failure this codebase is
     * built to make impossible.
     *
     * The tick is a detour on `PlayerSave.Update`, and that choice is not arbitrary. A Unity message
     * is dispatched into the component BY THE ENGINE, from outside the assembly, every frame — so it
     * can be neither inlined away nor routed around, which is exactly how this project burned a
     * build/deploy/relaunch cycle on a hook that resolved, applied, censused green, and never fired
     * (see knowledge/spiritvale/il2cpp-hook-resolution-is-not-execution.md). It also hands us a
     * guaranteed MAIN-THREAD tick, which is the only place `BagSnapshot`'s rows may be read.
     *
     * The consequence, said in the log rather than discovered: `PlayerSave` exists once you are in
     * the world, so the hotkey and the live bag arm after login. The URL is logged at boot for
     * exactly that reason, and the rule editing loop works before then either way — the page loads
     * and saves your filter with no bag in it.
     */
    private static void BindTick(string hotkey)
    {
        string wanted = ResolveHotkey(hotkey);

        IntPtr playerSave = Il2CppMeta.FindClass("", "PlayerSave", HookCensus.GameAssemblies);
        Il2CppMeta.MethodInfo? update = Il2CppMeta.FindMethodRuntime(playerSave, "Update", 0);
        if (update is null || update.NativePtr == IntPtr.Zero)
        {
            _log("editor tick NOT installed: PlayerSave.Update did not resolve, so there is no per-frame "
               + $"main-thread tick. The {wanted} hotkey is off, the served editor cannot show your live "
               + "bag or the item catalog, AND no loot pickup sound can play — the watcher rides this "
               + $"same tick. The editor still loads and saves your rules, from {Url}. Everything the "
               + "mod draws in game is unaffected.");
            return;
        }

        try
        {
            _updateHook = UpdateDetour;
            _updateDetour = Detours.Apply(update.NativePtr, _updateHook, out UpdateFn? original);
            _updateOriginal = original;
            Ticking = true;
        }
        catch (Exception e)
        {
            _updateHook = null;
            _updateOriginal = null;
            Ticking = false;
            _log($"editor tick NOT installed — {e.Message}. The hotkey is off, no loot pickup sound can "
               + $"play, and the served editor will have no bag; it still loads and saves your rules, "
               + $"from {Url}.");
            return;
        }

        if (_getKeyDown is not null && _hotkeyCode >= 0)
        {
            _log($"editor hotkey {wanted} (KeyCode {_hotkeyCode.ToString(CultureInfo.InvariantCulture)}) opens "
               + $"{Url} in your default browser. It arms once you are in the world; that address works at "
               + "any time if you would rather paste it.");
        }
        else
        {
            _log($"editor live bag on; hotkey off. Open {Url} yourself — see the line above for why the key "
               + "could not be read.");
        }
    }

    /**
     * `Input.GetKeyDown` and the `KeyCode` the player named. Returns the name for the log lines.
     *
     * `KeyCode` is read from the LIVE enum by name. Never hardcode an ordinal — `StatType` already
     * taught this codebase that enum order moves between builds, and a hardcoded 289 would silently
     * become some other key after a Unity upgrade, which is the worst class of bug here: everything
     * still works and the answer is about something else.
     */
    private static string ResolveHotkey(string hotkey)
    {
        string wanted = (hotkey ?? "").Trim();
        if (wanted.Length == 0) wanted = DefaultHotkey;

        IntPtr input = Il2CppMeta.FindClass("UnityEngine", "Input",
            "UnityEngine.InputLegacyModule.dll", "UnityEngine.CoreModule.dll", "UnityEngine.dll");
        IntPtr keyCode = Il2CppMeta.FindClass("UnityEngine", "KeyCode",
            "UnityEngine.CoreModule.dll", "UnityEngine.dll", "UnityEngine.InputLegacyModule.dll");

        // By parameter TYPE, never by arity: `GetKeyDown` also has a `(System.String)` overload, and
        // resolving an engine overload on arity is what took this game's process down once.
        Il2CppMeta.MethodInfo? getKeyDown = Il2CppMeta.FindOverload(input, "GetKeyDown", "UnityEngine.KeyCode");

        foreach ((string name, int value) in Il2CppMeta.EnumValues(keyCode))
        {
            if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) { _hotkeyCode = value; break; }
        }

        if (getKeyDown is null || getKeyDown.NativePtr == IntPtr.Zero)
        {
            _log($"editor hotkey NOT armed: UnityEngine.Input.GetKeyDown(KeyCode) did not resolve. Open "
               + $"{Url} yourself — everything else is unaffected.");
            return wanted;
        }
        if (_hotkeyCode < 0)
        {
            _log($"editor hotkey NOT armed: \"{wanted}\" is not a UnityEngine.KeyCode name. Use one of "
               + $"Unity's names (F8, F9, Insert, Backslash...) for Hotkey under [Editor]. Open {Url} "
               + "yourself in the meantime.");
            return wanted;
        }

        try { _getKeyDown = Marshal.GetDelegateForFunctionPointer<GetKeyDownFn>(getKeyDown.NativePtr); }
        catch (Exception e)
        {
            _getKeyDown = null;
            _log($"editor hotkey NOT armed — {e.Message}. Open {Url} yourself.");
        }
        return wanted;
    }

    /// <summary>The original first, always: the game's own frame must not depend on this mod.</summary>
    private static void UpdateDetour(IntPtr self)
    {
        try { _updateOriginal?.Invoke(self); }
        catch { /* never swallow the game's frame, but never rethrow into native code either */ }
        Tick(self);
    }

    /**
     * One frame's worth of work on the main thread, and it is deliberately almost nothing.
     *
     * Two integer comparisons, one `Input.GetKeyDown`, and the pickup watcher's own throttled check.
     * Publishing allocates only on the pass after the bag's content actually changed, and the catalog
     * is projected once a session.
     *
     * `playerSave` is the detour's `self`, and it is passed through rather than re-found because
     * <see cref="InventoryWatch"/> reads the player's inventory off it. This is the ONE hook on
     * `PlayerSave.Update`: a second detour on the same method would be two mods fighting over one
     * trampoline, so everything that wants a per-frame main-thread tick asks for it here.
     *
     * The whole body is guarded, because this runs inside a detour on a per-frame engine method: an
     * exception escaping into native code is a crash, not a stack trace. The first failure disarms
     * the tick and says so once — a log line per frame is worse than a dead hotkey.
     *
     * Internal rather than private so it can be driven directly by a test harness: the game holds
     * this DLL during development, so the only way to exercise a frame is to call the frame.
     */
    internal static void Tick(IntPtr playerSave)
    {
        if (_tickFailed) return;
        try
        {
            BagSnapshot.PublishToEditor();
            PublishCatalogIfChanged();
            InventoryWatch.Tick(playerSave);

            if (_getKeyDown is not null && _hotkeyCode >= 0 && _getKeyDown(_hotkeyCode, IntPtr.Zero) != 0)
            {
                Open();
            }
        }
        catch (Exception e)
        {
            _tickFailed = true;
            _log($"editor tick stopped after an error — {e.Message}. The hotkey, the live bag and the "
               + $"pickup sound are off for this session; {Url} still serves your rules. Nothing else "
               + "is affected.");
        }
    }

    /**
     * Open the editor in the player's default browser, at most once every couple of seconds.
     *
     * The debounce is the whole reason this is not two lines: `GetKeyDown` is true for one frame, but
     * a player taps a key twice, and `ShellExecute` takes long enough that the second tap lands
     * before the first window exists. Two browser windows on one keypress reads as a bug.
     *
     * `Process.Start` goes to the thread pool rather than running here. `ShellExecute` resolves a
     * protocol handler and may start a whole browser; doing that inside a per-frame engine callback
     * would hitch a frame for something the player is about to look away from the game for anyway.
     */
    private static void Open()
    {
        long now = Environment.TickCount64;
        if (now - _lastOpen < OpenDebounceMs) return;
        _lastOpen = now;

        string url = Url;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                // UseShellExecute is required: on .NET Core a bare URL is not an executable, and this
                // is the whole point — the player's own default browser, not one this mod picked.
                using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
            }
            catch (Exception e)
            {
                _log($"could not open a browser for {url} — {e.Message}. Paste that address into one.");
            }
        }, null);
    }

    /// <summary>`file:///C:/...` for a local path, so `Process.Start` opens it in a browser.</summary>
    private static string FileUrl(string path)
    {
        if (path.Length == 0) return "";
        try { return new Uri(path).AbsoluteUri; }
        catch { return path; }
    }
}
