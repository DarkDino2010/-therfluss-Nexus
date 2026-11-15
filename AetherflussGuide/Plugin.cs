using System.Numerics;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Text;
using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.Command;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;

namespace AetherflussGuide;

public sealed partial class Plugin : IDalamudPlugin
{
    private const string Command = "/aetherfluss";
    private const string PrivateMessageCommand = "/pn";
    private const string MiniChatCommand = "/afmini";
    // Set this to the HTTPS location where you deployed website-api/index.php.
    private const string DefaultApiBase = "https://der-aetherfluss.de/aetherfluss-api/index.php";
    private const string ConnectApi = "https://der-aetherfluss.de/aetherfluss-api/connect.php";
    private readonly Settings config;
    private string apiBase = DefaultApiBase;
    private bool configShown;
    private string connectionStatus = "Noch nicht geprüft.";
    private string categoryStatus = "Kategorien werden geladen ...";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IPlayerState playerState;
    private readonly ITextureProvider textures;
    private readonly IChatGui chatGui;
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private bool shown;
    private string query = "";
    private string status = "Bitte mindestens zwei Zeichen eingeben.";
    private List<SearchItem> matches = [];
    private List<CategoryItem> categories = [];
    private int selectedCategory = 0;
    private string guideListFilter = "";
    private int guideSelectedTab = 0;
    private EntryItem? selected;
    private readonly HashSet<int> favorites = [];
    private int requestId;
    private string inviteCode = "";
    private string registrationPassword = "";
    private string loginPassword = "";
    private string newPassword = "";
    private string currentPassword = "";
    private string deleteAccountPassword = "";
    private bool accountDeletionAvailable;
    private bool chatHasMore;
    private bool showSignup;
    private string connectStatus = "Noch nicht mit dem Connect-Server verbunden.";
    private string? accessToken;
    private bool connectBusy;
    private long registeredUserId;
    private string registeredCharacter = "";
    private string registeredWorld = "";
    private string friendSearch = "";
    private string chatDraft = "";
    private string socialStatus = "Freundesliste noch nicht geladen.";
    private List<FriendItem> friendItems = [];
    private List<UserItem> foundUsers = [];
    private List<ChatItem> chatItems = [];
    private long chatPartnerId;
    private string chatPartnerName = "";
    private bool socialBusy;
    private int? worldNextOffset;
    private string worldStatus = "Nur registrierte Ätherfluss-Connect-Spieler werden angezeigt.";
    private List<UserItem> worldUsers = [];
    private List<InboxItem> unreadMessages = [];
    private string inboxStatus = "Ungelesene Nachrichten noch nicht geprüft.";
    private DateTime nextInboxCheckUtc = DateTime.UtcNow.AddSeconds(5);
    private bool inboxBusy;
    private string pnStatus = "";
    private string pnRecipient = "";
    private string pnComposeText = "";
    private bool showPnComposer;
    private string directoryWorld = "";
    private string directoryQuery = "";
    private List<string> availableWorlds = [];
    private List<UserItem> directoryUsers = [];
    private int? directoryNextOffset;
    private string directoryStatus = "Welten und Kontakte noch nicht geladen.";
    private bool directoryBusy;
    private bool directoryVisible;
    private bool directorySettingsLoaded;
    private readonly HashSet<long> alertedMessageIds = [];
    private readonly Queue<string> pendingChatNotices = new();



    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IPlayerState playerState, IChatGui chatGui, ICondition condition, ITextureProvider textures)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.playerState = playerState;
        this.textures = textures;
        this.chatGui = chatGui;
        this.condition = condition;
        config = pluginInterface.GetPluginConfig() as Settings ?? new Settings();
        apiBase = IsAllowedApi(config.ApiUrl) ? config.ApiUrl : DefaultApiBase;
        if (!string.IsNullOrWhiteSpace(config.EncryptedConnectToken))
        {
            try
            {
                var encrypted = Convert.FromBase64String(config.EncryptedConnectToken);
                accessToken = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
            }
            catch (Exception)
            {
                accessToken = null;
                connectStatus = "Gespeicherte Sitzung kann nicht entsperrt werden. Erneut registrieren ist nicht automatisch möglich; bitte Administrator kontaktieren.";
            }
        }
        if (accessToken is not null) _ = LoadConnectProfileAsync();
        commands.AddHandler(Command, new CommandInfo((_, _) => shown = !shown)
        {
            HelpMessage = "Öffnet den Ätherfluss-Instanzguide."
        });
        commands.AddHandler(PrivateMessageCommand, new CommandInfo((command, args) => _ = SendPrivateCommandAsync(args))
        {
            HelpMessage = "/pn Charaktername@Heimatwelt Nachricht – nur an bestätigte Connect-Freunde."
        });
        commands.AddHandler(MiniChatCommand, new CommandInfo((_, _) =>
        {
            config.MiniChatEnabled = true;
            miniChatVisible = !miniChatVisible;
            pluginInterface.SavePluginConfig(config);
        }) { HelpMessage = "Ätherfluss-Mini-Chat ein- oder ausblenden." });
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += Open;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        _ = LoadCategoriesAsync();
    }

    private void Open() => shown = true;
    private void OpenConfig() => configShown = true;

    private static bool IsAllowedApi(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("der-aetherfluss.de", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.EndsWith("/index.php", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private async Task<T> RequestAsync<T>(string url)
    {
        using var response = await client.GetAsync(url, lifetime.Token);
        var body = await response.Content.ReadAsStringAsync(lifetime.Token);
        if (!response.IsSuccessStatusCode)
        {
            string details = response.ReasonPhrase ?? "Unbekannt";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error))
                    details = error.GetString() ?? details;
            }
            catch (JsonException) { }
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {details}");
        }
        try
        {
            return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Leere API-Antwort");
        }
        catch (JsonException e) { throw new JsonException("Ungültige JSON-Antwort: " + e.Message, e); }
    }

    private async Task TestConnectionAsync()
    {
        lock (gate) connectionStatus = "Verbindung wird geprüft ...";
        try
        {
            var payload = await RequestAsync<CategoryResponse>($"{apiBase}?action=categories");
            lock (gate) connectionStatus = $"Verbindung OK – {payload.Categories.Count} Kategorien empfangen.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) connectionStatus = "Verbindungsfehler: " + ex.Message; }
    }

    private async Task SearchAsync(string term)
    {
        var ticket = Interlocked.Increment(ref requestId);
        lock (gate) status = "Suche läuft ...";
        try
        {
            var url = $"{apiBase}?action=search&q={Uri.EscapeDataString(term)}";
            var payload = await RequestAsync<SearchResponse>(url);
            lock (gate)
            {
                if (ticket != requestId || lifetime.IsCancellationRequested) return;
                matches = payload?.Results ?? [];
                selected = null;
                status = matches.Count == 0 ? "Keine Treffer gefunden." : $"{matches.Count} Treffer";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (gate) if (ticket == requestId) status = "Suche fehlgeschlagen: " + ex.Message;
        }
    }

    private async Task LoadCategoriesAsync()
    {
        try
        {
            var payload = await RequestAsync<CategoryResponse>($"{apiBase}?action=categories");
            lock (gate) { categories = payload.Categories; categoryStatus = $"{categories.Count} Kategorien geladen."; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) categoryStatus = "Kategorienfehler: " + ex.Message; }
    }

    private async Task BrowseCategoryAsync(int id)
    {
        var ticket = Interlocked.Increment(ref requestId);
        lock (gate) status = "Lade Kategorie ...";
        try
        {
            var payload = await RequestAsync<SearchResponse>($"{apiBase}?action=category&id={id}");
            lock (gate)
            {
                if (ticket != requestId || lifetime.IsCancellationRequested) return;
                matches = payload?.Results ?? [];
                selected = null;
                status = $"{matches.Count} Einträge in dieser Kategorie (maximal 100)";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) if (ticket == requestId) status = "Kategoriefehler: " + ex.Message; }
    }

    private async Task LoadAsync(int id)
    {
        var ticket = Interlocked.Increment(ref requestId);
        lock (gate) status = "Lade Guide ...";
        try
        {
            var payload = await RequestAsync<EntryResponse>($"{apiBase}?action=entry&id={id}");
            lock (gate)
            {
                if (ticket != requestId || lifetime.IsCancellationRequested) return;
                selected = payload?.Entry;
                guideSelectedTab = 0;
                status = selected is null ? "Kein Guide gefunden." : "Guide geladen";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (gate) if (ticket == requestId) status = "Guidefehler: " + ex.Message;
        }
    }

    private static void Wrapped(string text)
    {
        ImGui.TextWrapped(text);
    }

    private void Draw()
    {
        // ImGui Draw is called even when the guide window is closed or a duty is running.
        // Do not poll when Connect is disabled or a different character is logged in.
        if (HasMatchingConnectCharacter() && DateTime.UtcNow >= nextInboxCheckUtc && !inboxBusy)
        {
            nextInboxCheckUtc = DateTime.UtcNow.AddSeconds(30);
            _ = LoadInboxAsync();
        }
        lock (gate)
        {
            if (config.ShowChatNotifications && !(config.DeferNotificationsInCombat && condition[ConditionFlag.InCombat]))
            {
                int pending = pendingChatNotices.Count;
                if (pending > 5)
                {
                    chatGui.Print($"{pending} neue Ätherfluss-Hinweise. /aetherfluss zum Lesen.", "Ätherfluss Connect");
                    pendingChatNotices.Clear();
                }
                else for (int n = 0; n < 5 && pendingChatNotices.Count > 0; n++)
                    chatGui.Print(pendingChatNotices.Dequeue(), "Ätherfluss Connect");
            }
            else if (!config.ShowChatNotifications) pendingChatNotices.Clear();
        }
        SocialHubTick();
        DrawMiniChat();
        DrawConfig();
        if (!shown) return;
        ImGui.SetNextWindowSize(new Vector2(720, 550), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Ätherfluss Guide###AetherflussMain", ref shown)) { ImGui.End(); return; }
        if (ImGui.BeginTabBar("##aetherfluss-tabs"))
        {
            if (ImGui.BeginTabItem("Guides"))
            {
                DrawGuide();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Ätherfluss Connect"))
            {
                DrawConnect();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    // Guide V0.12.3: fully clickable cards, pill tabs and compact detail panels.
    // Only ImGui presentation is changed; Guide API and Connect remain untouched.
    private static readonly Vector4 GuideBlue = new(.43f, .75f, 1f, 1f);
    private static readonly Vector4 GuideMuted = new(.59f, .65f, .75f, 1f);
    private static readonly Vector4 GuidePanel = new(.095f, .12f, .17f, .94f);
    private static readonly Vector4 GuidePanelHover = new(.14f, .20f, .28f, .98f);
    private static readonly Vector4 GuidePanelActive = new(.12f, .25f, .37f, .98f);

    private static void GuideSectionTitle(string heading)
    {
        ImGui.Spacing();
        ImGui.TextColored(GuideBlue, heading);
        ImGui.Spacing();
    }

    private static bool GuidePill(string text, bool active)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 9f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 7f));
        ImGui.PushStyleColor(ImGuiCol.Button, active ? new Vector4(.17f, .37f, .54f, 1f) : GuidePanel);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, GuidePanelHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, GuidePanelActive);
        bool clicked = ImGui.Button(text);
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(2);
        return clicked;
    }

    private static void GuideInfoTile(string key, string value, int index, float width)
    {
        ImGui.PushID(index);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, GuidePanel);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 9f);
        if (ImGui.BeginChild("##guide-info-tile", new Vector2(width, 65f), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.SetCursorPos(new Vector2(11f, 8f));
            ImGui.TextColored(GuideBlue, key);
            ImGui.SetCursorPosX(11f);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - 23f);
            ImGui.TextUnformatted(value.Length > 62 ? value[..59] + "…" : value);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        ImGui.PopID();
    }

    private void DrawGuide()
    {
        ImGui.TextColored(GuideBlue, "ÄTHERFLUSS  /  GUIDE-BIBLIOTHEK");
        ImGui.SameLine();
        if (GuidePill("⚙ Einstellungen", false)) configShown = true;
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-105);
        bool submitted = ImGui.InputTextWithHint("##guidequery", "Dungeon, Prüfung, Raid oder Boss suchen ...", ref query, 120,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((GuidePill("Suchen", false) || submitted) && query.Trim().Length >= 2)
            _ = SearchAsync(query.Trim());

        List<CategoryItem> localCategories;
        List<SearchItem> localMatches;
        EntryItem? localEntry;
        string localStatus, localCategoryStatus;
        lock (gate)
        {
            localCategories = [.. categories]; localMatches = [.. matches];
            localEntry = selected; localStatus = status; localCategoryStatus = categoryStatus;
        }
        ImGui.SetNextItemWidth(MathF.Max(210f, ImGui.GetContentRegionAvail().X - 132f));
        if (ImGui.BeginCombo("##guideCategory", selectedCategory == 0 ? "Alle Kategorien" :
            localCategories.FirstOrDefault(x => x.Id == selectedCategory)?.Title ?? "Kategorie"))
        {
            if (ImGui.Selectable("Alle Kategorien", selectedCategory == 0))
            {
                selectedCategory = 0;
                if (query.Trim().Length >= 2) _ = SearchAsync(query.Trim());
            }
            foreach (var category in localCategories)
            {
                var name = category.ParentId == 122 ? "   ↳ " + category.Title : category.Title;
                if (ImGui.Selectable($"{name}##{category.Id}", selectedCategory == category.Id))
                { selectedCategory = category.Id; _ = BrowseCategoryAsync(category.Id); }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (GuidePill("↻ Kategorien", false)) _ = LoadCategoriesAsync();
        ImGui.TextDisabled(localCategoryStatus);
        ImGui.Spacing();

        var areaHeight = ImGui.GetContentRegionAvail().Y;
        var sidebarWidth = MathF.Min(320f, MathF.Max(215f, ImGui.GetContentRegionAvail().X * .34f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.055f, .075f, .11f, .68f));
        if (ImGui.BeginChild("##guide-card-column", new Vector2(sidebarWidth, areaHeight), false))
        {
            GuideSectionTitle($"ENTDECKEN  ·  {localMatches.Count} Einträge");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filterGuide", "Treffer filtern ...", ref guideListFilter, 80);
            ImGui.TextDisabled(localStatus);
            ImGui.Spacing();
            var filtered = localMatches.Where(x => x.Title.Contains(guideListFilter, StringComparison.OrdinalIgnoreCase));
            foreach (var entry in filtered)
            {
                bool active = localEntry?.Id == entry.Id;
                ImGui.PushID(entry.Id);
                var top = ImGui.GetCursorScreenPos();
                float width = ImGui.GetContentRegionAvail().X;
                const float cardHeight = 91f;
                ImGui.InvisibleButton("##guide-card-click", new Vector2(width, cardHeight));
                bool hovered = ImGui.IsItemHovered();
                if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _ = LoadAsync(entry.Id);
                if (hovered && !string.IsNullOrWhiteSpace(entry.Summary)) ImGui.SetTooltip(entry.Summary);
                var bottom = top + new Vector2(width, cardHeight);
                var color = active ? GuidePanelActive : hovered ? GuidePanelHover : GuidePanel;
                var painter = ImGui.GetWindowDrawList();
                painter.AddRectFilled(top, bottom, ImGui.ColorConvertFloat4ToU32(color), 10f);
                if (active) painter.AddRectFilled(top + new Vector2(0f, 8f), top + new Vector2(4f, cardHeight - 8f),
                    ImGui.ColorConvertFloat4ToU32(GuideBlue), 2f);
                painter.AddText(top + new Vector2(13f, 9f), ImGui.ColorConvertFloat4ToU32(GuideBlue),
                    active ? "◆  AUSGEWÄHLT" : "◆  GUIDE");
                // Use ImGui text wrapping in the normal font, clipped to the card's space.
                var saved = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(top + new Vector2(13f, 30f));
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - 26f);
                ImGui.TextUnformatted(entry.Title.Length > 68 ? entry.Title[..65] + "…" : entry.Title);
                ImGui.PopTextWrapPos();
                ImGui.SetCursorScreenPos(saved);
                painter.AddText(top + new Vector2(13f, 71f), ImGui.ColorConvertFloat4ToU32(GuideMuted), $"ARTIKEL  #{entry.Id}");
                if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.PopID();
                ImGui.Spacing();
            }
            if (localMatches.Count == 0) ImGui.TextWrapped("Wähle eine Kategorie oder suche nach einem Guide.");
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##guide-detail", new Vector2(0, areaHeight), false))
        {
            if (localEntry == null)
            {
                GuideSectionTitle("ÄTHERFLUSS GUIDE");
                ImGui.Spacing();
                Wrapped("Wähle links einen Guide aus. Übersicht, Voraussetzungen, Mechaniken und Videos erscheinen hier als klar gegliederte Bereiche.");
            }
            else
            {
                ImGui.TextColored(GuideBlue, "GUIDE  /  FINAL FANTASY XIV");
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(localEntry.Title);
                ImGui.PopTextWrapPos();
                ImGui.Spacing();
                if (GuidePill(favorites.Contains(localEntry.Id) ? "★ Favorit entfernen" : "☆ Favorit", false))
                {
                    if (!favorites.Add(localEntry.Id)) favorites.Remove(localEntry.Id);
                }
                ImGui.SameLine();
                if (GuidePill("Originalartikel ↗", false)) OpenAetherGuideLink(localEntry.Url);
                ImGui.Spacing();
                if (GuidePill("Übersicht", guideSelectedTab == 0)) guideSelectedTab = 0;
                ImGui.SameLine();
                if (GuidePill("Mechaniken", guideSelectedTab == 1)) guideSelectedTab = 1;
                ImGui.SameLine();
                int count = localEntry.VideoItems?.Count ?? localEntry.Videos?.Count ?? 0;
                if (GuidePill($"Videos ({count})", guideSelectedTab == 2)) guideSelectedTab = 2;
                ImGui.Spacing();
                if (guideSelectedTab == 0)
                {
                    GuideSectionTitle("ÜBERBLICK");
                    Wrapped(!string.IsNullOrWhiteSpace(localEntry.Summary) ? localEntry.Summary : "Für diesen Guide ist keine Kurzbeschreibung hinterlegt.");
                    if (localEntry.Fields is { Count: > 0 })
                    {
                        GuideSectionTitle("VORAUSSETZUNGEN & INFORMATIONEN");
                        float available = ImGui.GetContentRegionAvail().X;
                        float tileWidth = available >= 380f ? (available - ImGui.GetStyle().ItemSpacing.X) / 2f : available;
                        int index = 0;
                        foreach (var pair in localEntry.Fields)
                        {
                            if (tileWidth < available && index % 2 == 1) ImGui.SameLine();
                            GuideInfoTile(pair.Key, string.Join(" · ", pair.Value), index, tileWidth);
                            index++;
                        }
                    }
                }
                else if (guideSelectedTab == 1)
                {
                    GuideSectionTitle("GUIDE & MECHANIKEN");
                    if (localEntry.Sections is { Count: > 0 })
                    {
                        int index = 0;
                        foreach (var section in localEntry.Sections)
                        {
                            ImGui.PushID(index++);
                            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
                            if (ImGui.CollapsingHeader(section.Title + "##mechanic", ImGuiTreeNodeFlags.DefaultOpen))
                            { ImGui.Indent(10f); Wrapped(section.Text); ImGui.Unindent(10f); ImGui.Spacing(); }
                            ImGui.PopStyleVar();
                            ImGui.PopID();
                        }
                    }
                    else Wrapped("Keine separaten Mechanik-Abschnitte gefunden. Den vollständigen Artikel kannst du oben öffnen.");
                }
                else
                {
                    GuideSectionTitle($"VIDEOGUIDES  ·  {count}");
                    if (!videoPreviewsAllowed)
                    {
                        ImGui.TextDisabled("Vorschaubilder werden erst nach Zustimmung von YouTube geladen.");
                        if (ImGui.Button("Videovorschauen laden (YouTube)")) videoPreviewsAllowed = true;
                        ImGui.Spacing();
                    }
                    else
                    {
                        ImGui.TextDisabled("Videovorschauen von YouTube aktiviert (nur für diese Plugin-Sitzung).");
                        ImGui.Spacing();
                    }
                    if (localEntry.VideoItems is { Count: > 0 })
                    {
                        int index = 0;
                        foreach (var video in localEntry.VideoItems)
                        {
                            ImGui.PushID(index++);
                            ImGui.PushStyleColor(ImGuiCol.ChildBg, GuidePanel);
                            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 9f);
                            float availableWidth = Math.Max(260f, ImGui.GetContentRegionAvail().X);
                            float thumbWidth = availableWidth > 440f ? 184f : 120f;
                            float thumbHeight = thumbWidth * 9f / 16f;
                            float tileHeight = Math.Max(128f, thumbHeight + 22f);
                            if (ImGui.BeginChild("##video-tile", new Vector2(-1, tileHeight), false))
                            {
                                ImGui.SetCursorPos(new Vector2(10f, 12f));
                                DrawVideoThumbnail(video.Url, new Vector2(thumbWidth, thumbHeight));
                                ImGui.SameLine(0f, 14f);
                                ImGui.BeginGroup();
                                ImGui.TextColored(GuideBlue, video.Source == "easymedia" ? "EASYMEDIA / YOUTUBE" : "YOUTUBE");
                                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(120f, availableWidth - thumbWidth - 48f));
                                ImGui.TextUnformatted(video.Title);
                                ImGui.PopTextWrapPos();
                                if (!string.IsNullOrWhiteSpace(video.MediaTitle) && video.MediaTitle != video.Title)
                                {
                                    ImGui.TextDisabled(video.MediaTitle);
                                }
                                if (IsYoutubeVideo(video.Url))
                                {
                                    if (ImGui.SmallButton("▶ Video im Browser öffnen ↗")) Util.OpenLink(video.Url);
                                }
                                else if (ImGui.SmallButton("Im Originalguide öffnen ↗")) OpenAetherGuideLink(localEntry.Url);
                                ImGui.EndGroup();
                            }
                            ImGui.EndChild();
                            ImGui.PopStyleVar();
                            ImGui.PopStyleColor();
                            ImGui.PopID();
                            ImGui.Spacing();
                        }
                        ImGui.TextDisabled("Videowiedergabe weiterhin im Browser. Keine eingebettete YouTube-Wiedergabe.");
                    }
                    else if (localEntry.Videos is { Count: > 0 })
                    {
                        int index = 0;
                        foreach (var video in localEntry.Videos)
                        {
                            ImGui.PushID(index++);
                            if (IsYoutubeVideo(video) && GuidePill("▶ YouTube-Video im Browser ansehen ↗", false)) Util.OpenLink(video);
                            ImGui.PopID();
                        }
                    }
                    else Wrapped("Keine Videoeinbindungen in diesem Guide erkannt.");
                }
                ImGui.Spacing();
                DrawGuideShare(localEntry);
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private static bool IsYoutubeVideo(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase));

    private static void OpenAetherGuideLink(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("der-aetherfluss.de", StringComparison.OrdinalIgnoreCase))
            Util.OpenLink(uri.ToString());
    }

    // Pilot: the server accepts invite codes but does not independently verify FFXIV character ownership.
    // Do not describe this as verified character authentication or enable it for public signups.
    private async Task<(HttpStatusCode Code, string Json)> ConnectRequestAsync(string action, HttpMethod method, object? data = null, bool authenticate = false)
    {
        // A POST JSON body is the primary credential transport on shared hosting.
        // Headers are retained for backwards compatibility with older servers.
        var actualMethod = authenticate ? HttpMethod.Post : method;
        using var request = new HttpRequestMessage(actualMethod, ConnectApi + "?action=" + action);
        if (authenticate)
        {
            string token = accessToken ?? throw new InvalidOperationException("Keine Connect-Sitzung vorhanden.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Aether-Connect-Token", token);
            var payload = new Dictionary<string, object?> { ["accessToken"] = token };
            if (data is not null)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(data));
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("accessToken")) continue;
                    payload[property.Name] = property.Value.Clone();
                }
            }
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }
        else if (data is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
        }
        using var response = await client.SendAsync(request, lifetime.Token);
        string json = await response.Content.ReadAsStringAsync(lifetime.Token);
        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: API-Weiterleitung erkannt. Bitte die HTTPS-API-Adresse pruefen.");
        if (!response.IsSuccessStatusCode)
        {
            string detail = response.ReasonPhrase ?? "Unbekannter Fehler";
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("error", out var error)) detail = error.GetString() ?? detail;
            }
            catch (JsonException) { }
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {detail}");
        }
        return (response.StatusCode, json);
    }

    private async Task ConnectHealthAsync()
    {
        lock (gate) { if (connectBusy) return; connectBusy = true; connectStatus = "Prüfe Connect-API ..."; }
        try
        {
            var response = await ConnectRequestAsync("health", HttpMethod.Get);
            using var doc = JsonDocument.Parse(response.Json);
            string db = doc.RootElement.GetProperty("database").GetString() ?? "unbekannt";
            bool deleteAvailable = doc.RootElement.TryGetProperty("accountDeletionAvailable", out var flag)
                && flag.ValueKind == JsonValueKind.True;
            lock (gate)
            {
                accountDeletionAvailable = deleteAvailable;
                connectStatus = "Connect API: " + db + (deleteAvailable ? " · Kontolöschung verfügbar" : " · Kontolöschung noch nicht verfügbar");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) connectStatus = "Verbindungsfehler: " + ex.Message; }
        finally { lock (gate) connectBusy = false; }
    }

    private async Task RegisterConnectAsync(string character, string home, string code, string password)
    {
        lock (gate) { if (connectBusy) return; connectBusy = true; connectStatus = "Registrierung läuft ..."; }
        try
        {
            var response = await ConnectRequestAsync("register", HttpMethod.Post,
                new { characterName = character, homeWorld = home, inviteCode = code, password });
            using var doc = JsonDocument.Parse(response.Json);
            var token = doc.RootElement.GetProperty("accessToken").GetString()
                ?? throw new JsonException("Sitzungstoken fehlt.");
            var user = doc.RootElement.GetProperty("user");
            var id = user.GetProperty("id").GetInt64();
            var verifiedName = user.GetProperty("characterName").GetString() ?? "";
            var verifiedWorld = user.GetProperty("homeWorld").GetString() ?? "";
            // Keep token encrypted for the current Windows user, never log or display it.
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            config.EncryptedConnectToken = Convert.ToBase64String(protectedBytes);
            pluginInterface.SavePluginConfig(config);
            lock (gate)
            {
                accessToken = token;
                registeredUserId = id;
                registeredCharacter = verifiedName;
                registeredWorld = verifiedWorld;
                connectStatus = "Pilot-Registrierung erfolgreich. Keine FFXIV-Eigentumsverifikation!";
                inviteCode = "";
                registrationPassword = "";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) connectStatus = "Registrierung fehlgeschlagen: " + ex.Message; }
        finally { lock (gate) connectBusy = false; }
    }

    private async Task LoginConnectAsync(string character, string home, string password)
    {
        lock (gate) { if (connectBusy) return; connectBusy = true; connectStatus = "Melde an ..."; }
        try
        {
            var response = await ConnectRequestAsync("login", HttpMethod.Post,
                new { characterName = character, homeWorld = home, password });
            using var doc = JsonDocument.Parse(response.Json);
            var token = doc.RootElement.GetProperty("accessToken").GetString()
                ?? throw new JsonException("Sitzungstoken fehlt.");
            var user = doc.RootElement.GetProperty("user");
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            config.EncryptedConnectToken = Convert.ToBase64String(encrypted);
            pluginInterface.SavePluginConfig(config);
            lock (gate)
            {
                accessToken = token;
                registeredUserId = user.GetProperty("id").GetInt64();
                registeredCharacter = user.GetProperty("characterName").GetString() ?? "";
                registeredWorld = user.GetProperty("homeWorld").GetString() ?? "";
                loginPassword = "";
                connectStatus = "Connect-Anmeldung erfolgreich.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) connectStatus = "Anmeldung fehlgeschlagen: " + ex.Message; }
        finally { lock (gate) connectBusy = false; }
    }

    private async Task SetConnectPasswordAsync(string oldPassword, string password)
    {
        lock (gate) { if (connectBusy) return; connectBusy = true; connectStatus = "Speichere Passwort ..."; }
        try
        {
            await ConnectRequestAsync("set_password", HttpMethod.Post,
                new { currentPassword = oldPassword, newPassword = password }, authenticate: true);
            lock (gate)
            {
                newPassword = "";
                currentPassword = "";
                connectStatus = "Passwort gespeichert. Beim nächsten Anmelden verwendbar.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) connectStatus = "Passwortwechsel fehlgeschlagen: " + ex.Message; }
        finally { lock (gate) connectBusy = false; }
    }

    private async Task DeleteConnectAccountAsync(string password)
    {
        lock (gate)
        {
            if (connectBusy || !accountDeletionAvailable || !HasMatchingConnectCharacter()) return;
            connectBusy = true;
            connectStatus = "Konto wird auf dem Server gelöscht ...";
        }
        try
        {
            await ConnectRequestAsync("delete_account", HttpMethod.Post, new { password }, authenticate: true);
            // Only clear the local account after the server confirmed the deletion.
            lock (gate)
            {
                accessToken = null;
                registeredUserId = 0;
                registeredCharacter = "";
                registeredWorld = "";
                friendItems.Clear();
                foundUsers.Clear();
                chatItems.Clear();
                worldUsers.Clear();
                directoryUsers.Clear();
                friendPresence.Clear();
                contactExtras.Clear();
                raidEvents.Clear();
                translatedMessages.Clear();
                editContactId = 0;
                guideShareRecipientId = 0;
                raidInviteeId = 0;
                miniChatVisible = false;
                unreadMessages.Clear();
                alertedMessageIds.Clear();
                pendingChatNotices.Clear();
                chatPartnerId = 0;
                chatPartnerName = "";
                chatDraft = "";
                loginPassword = "";
                registrationPassword = "";
                currentPassword = "";
                newPassword = "";
                deleteAccountPassword = "";
                accountDeletionAvailable = false;
                chatHasMore = false;
                connectStatus = "Connect-Konto wurde vom Server gelöscht. Neue Registrierung benötigt einen Einladungscode.";
            }
            config.EncryptedConnectToken = "";
            pluginInterface.SavePluginConfig(config);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (gate) connectStatus = "Kontolöschung fehlgeschlagen: " + ex.Message;
        }
        finally { lock (gate) connectBusy = false; }
    }

    private async Task LoadConnectProfileAsync()
    {
        lock (gate) { if (connectBusy) return; connectBusy = true; connectStatus = "Lade Profil ..."; }
        try
        {
            var response = await ConnectRequestAsync("me", HttpMethod.Get, authenticate: true);
            using var doc = JsonDocument.Parse(response.Json);
            var user = doc.RootElement.GetProperty("user");
            lock (gate)
            {
                registeredUserId = user.GetProperty("id").GetInt64();
                registeredCharacter = user.GetProperty("characterName").GetString() ?? "";
                registeredWorld = user.GetProperty("homeWorld").GetString() ?? "";
                connectStatus = "Connect-Profil geladen.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) connectStatus = "Profil konnte nicht geladen werden: " + ex.Message; }
        finally { lock (gate) connectBusy = false; }
    }

    private async Task LogoutConnectAsync()
    {
        lock (gate) { if (connectBusy) return; connectBusy = true; connectStatus = "Melde ab ..."; }
        try
        {
            await ConnectRequestAsync("logout", HttpMethod.Post, authenticate: true);
            lock (gate) connectStatus = "Abgemeldet.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) connectStatus = "Server-Abmeldung nicht bestätigt: " + ex.Message; }
        finally
        {
            // Always remove the local token; if server logout failed it remains valid until expiry.
            lock (gate)
            {
                accessToken = null;
                registeredUserId = 0;
                registeredCharacter = "";
                registeredWorld = "";
                unreadMessages.Clear();
                alertedMessageIds.Clear();
                pendingChatNotices.Clear();
                friendItems.Clear();
                foundUsers.Clear();
                chatItems.Clear();
                worldUsers.Clear();
                directoryUsers.Clear();
                friendPresence.Clear();
                contactExtras.Clear();
                raidEvents.Clear();
                translatedMessages.Clear();
                editContactId = 0;
                guideShareRecipientId = 0;
                raidInviteeId = 0;
                miniChatVisible = false;
                chatPartnerId = 0;
                chatPartnerName = "";
                chatDraft = "";
                loginPassword = "";
                registrationPassword = "";
                newPassword = "";
                currentPassword = "";
                deleteAccountPassword = "";
                chatHasMore = false;
                directorySettingsLoaded = false;
                connectBusy = false;
            }
            config.EncryptedConnectToken = "";
            pluginInterface.SavePluginConfig(config);
        }
    }

    private void DrawConnect()
    {
        ImGui.TextUnformatted("Ätherfluss Connect – Anmeldung und Registrierung");
        ImGui.Separator();
        if (!playerState.IsLoaded || string.IsNullOrWhiteSpace(playerState.CharacterName))
        {
            ImGui.TextWrapped("Bitte zuerst mit einem FFXIV-Charakter einloggen.");
            return;
        }
        string character = playerState.CharacterName;
        string home = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : "";
        string current = playerState.CurrentWorld.IsValid ? playerState.CurrentWorld.Value.Name.ToString() : "";
        ImGui.TextUnformatted("Charakter: " + character);
        ImGui.TextUnformatted("Heimatserver: " + (home.Length > 0 ? home : "Unbekannt"));
        ImGui.TextDisabled("Aktueller Server: " + current);
        ImGui.Spacing();
        var optIn = config.ConnectOptIn;
        if (ImGui.Checkbox("Connect aktivieren (freiwillige Serverübertragung)", ref optIn))
        {
            config.ConnectOptIn = optIn;
            pluginInterface.SavePluginConfig(config);
        }
        ImGui.TextWrapped("Bei Registrierung werden Name und Heimatserver an der-aetherfluss.de übertragen. " +
            "Der Einladungscode begrenzt den Zugang, beweist aber NICHT den Besitz des FFXIV-Charakters.");
        if (ImGui.Button("Connect-API prüfen")) _ = ConnectHealthAsync();
        ImGui.Separator();
        string message; string? token; long id; string registeredName; string registeredHome; bool busy;
        lock (gate)
        {
            message = connectStatus;
            token = accessToken;
            id = registeredUserId;
            registeredName = registeredCharacter;
            registeredHome = registeredWorld;
            busy = connectBusy;
        }
        ImGui.TextWrapped(message);
        if (token is not null)
        {
            ImGui.TextUnformatted(id > 0 ? $"Registriertes Profil: {registeredName}@{registeredHome} (ID {id})" : "Gespeicherte Sitzung vorhanden.");
            if (!string.Equals(character, registeredName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(home, registeredHome, StringComparison.OrdinalIgnoreCase))
                ImGui.TextWrapped("Achtung: Angemeldeter Charakter weicht vom Connect-Profil ab. Keine Chatfunktionen für diesen Charakter aktivieren.");
            ImGui.TextDisabled("Kontoverwaltung und Passwort findest du im Tab Konto & Sicherheit.");
        }
        else
        {
            ImGui.TextUnformatted($"Anmeldung für {character}@{home}");
            ImGui.TextDisabled("Name und Heimatwelt kommen vom aktuell eingeloggten Charakter.");
            ImGui.Checkbox("Neues Connect-Konto erstellen", ref showSignup);
            if (showSignup)
            {
                ImGui.TextUnformatted("Einladungscode (48 Hex-Zeichen)");
                ImGui.SetNextItemWidth(420);
                ImGui.InputText("##invitation", ref inviteCode, 64);
                ImGui.InputText("Passwort (mind. 12 Zeichen)##signup", ref registrationPassword, 128, ImGuiInputTextFlags.Password);
                bool valid = inviteCode.Trim().Length == 48 && inviteCode.Trim().All(Uri.IsHexDigit);
                if (!busy && config.ConnectOptIn && valid && registrationPassword.Length >= 12 && home.Length > 0 && ImGui.Button("Konto registrieren"))
                    _ = RegisterConnectAsync(character, home, inviteCode.Trim(), registrationPassword);
            }
            else
            {
                ImGui.InputText("Connect-Passwort##login", ref loginPassword, 128, ImGuiInputTextFlags.Password);
                if (!busy && config.ConnectOptIn && loginPassword.Length > 0 && home.Length > 0 && ImGui.Button("Mit Charakter anmelden"))
                    _ = LoginConnectAsync(character, home, loginPassword);
                ImGui.TextDisabled("Bestehendes Konto ohne Passwort? Alte Sitzung öffnen und Passwort festlegen.");
            }
            if (!config.ConnectOptIn) ImGui.TextDisabled("Vorher Connect aktivieren.");
        }
        ImGui.Spacing();
        if (token is not null && id > 0 && config.ConnectOptIn &&
            string.Equals(character, registeredName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(home, registeredHome, StringComparison.OrdinalIgnoreCase))
        {
            ImGui.Separator();
            DrawSocial();
        }
        else ImGui.TextDisabled("Freundesliste nur mit aktivem, passendem Connect-Profil.");
    }

    private async Task SocialOperationAsync(string action, HttpMethod method, object? data = null)
    {
        lock (gate) { if (socialBusy) return; socialBusy = true; socialStatus = "Anfrage läuft ..."; }
        try
        {
            var response = await ConnectRequestAsync(action, method, data, authenticate: true);
            using var doc = JsonDocument.Parse(response.Json);
            lock (gate)
            {
                if (action == "friends")
                {
                    friendItems = JsonSerializer.Deserialize<List<FriendItem>>(doc.RootElement.GetProperty("friends").GetRawText(), SocialJson) ?? [];
                    socialStatus = $"{friendItems.Count} Kontakte/Anfragen geladen.";
                }
                else if (action.StartsWith("users&", StringComparison.Ordinal) || action == "users")
                {
                    foundUsers = JsonSerializer.Deserialize<List<UserItem>>(doc.RootElement.GetProperty("users").GetRawText(), SocialJson) ?? [];
                    socialStatus = $"{foundUsers.Count} Spieler gefunden.";
                }
                else if (action.StartsWith("messages&", StringComparison.Ordinal) || action == "messages")
                {
                    var received = JsonSerializer.Deserialize<List<ChatItem>>(doc.RootElement.GetProperty("messages").GetRawText(), SocialJson) ?? [];
                    bool olderPage = action.Contains("&beforeId=", StringComparison.Ordinal);
                    if (olderPage)
                        chatItems = received.Concat(chatItems).DistinctBy(m => m.Id).OrderBy(m => m.Id).ToList();
                    else
                        chatItems = received;
                    chatHasMore = doc.RootElement.TryGetProperty("hasMore", out var more)
                        && more.ValueKind == JsonValueKind.True;
                    socialStatus = $"{chatItems.Count} Nachrichten geladen.";
                }
                else
                {
                    socialStatus = "Aktion erfolgreich. Freundesliste aktualisieren.";
                    if (action == "message_send") chatDraft = "";
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) socialStatus = "Connect-Fehler: " + ex.Message; }
        finally { lock (gate) socialBusy = false; }
    }

    private static readonly JsonSerializerOptions SocialJson = new() { PropertyNameCaseInsensitive = true };

    private bool HasMatchingConnectCharacter()
    {
        if (accessToken is null || registeredUserId <= 0 || !config.ConnectOptIn || !playerState.IsLoaded || !playerState.HomeWorld.IsValid) return false;
        var character = playerState.CharacterName.ToString();
        var world = playerState.HomeWorld.Value.Name.ToString();
        return string.Equals(character, registeredCharacter, StringComparison.OrdinalIgnoreCase)
            && string.Equals(world, registeredWorld, StringComparison.OrdinalIgnoreCase);
    }

    // Manual /pn command: resolve a friend uniquely by name + optional @world.
    // No message goes to an unconfirmed stranger, regardless of an arbitrary search result.
    private async Task SendPrivateCommandAsync(string args)
    {
        if (!HasMatchingConnectCharacter()) { lock (gate) pnStatus = "Connect anmelden und mit passendem Charakter einloggen."; return; }
        if (string.IsNullOrWhiteSpace(args))
        {
            lock (gate) { shown = true; showPnComposer = true; pnStatus = "Empfänger auswählen und Nachricht verfassen."; }
            await SocialOperationAsync("friends", HttpMethod.Get);
            return;
        }
        try
        {
            var response = await ConnectRequestAsync("friends", HttpMethod.Get, authenticate: true);
            using var doc = JsonDocument.Parse(response.Json);
            var friends = JsonSerializer.Deserialize<List<FriendItem>>(doc.RootElement.GetProperty("friends").GetRawText(), SocialJson) ?? [];
            var accepted = friends.Where(f => f.Status == "accepted").OrderByDescending(f => (f.CharacterName + "@" + f.HomeWorld).Length).ToList();
            string input = args.Trim();
            var candidates = new List<(FriendItem Friend, string Text)>();
            foreach (var friend in accepted)
            {
                foreach (var name in new[] { friend.CharacterName + "@" + friend.HomeWorld, friend.CharacterName })
                {
                    if (input.Length > name.Length && input.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add((friend, input[(name.Length + 1)..].Trim()));
                        break;
                    }
                }
            }
            if (candidates.Count == 0)
            {
                lock (gate)
                {
                    shown = true;
                    showPnComposer = true;
                    pnRecipient = input;
                    pnStatus = "Empfänger auswählen. Nachrichten sind nur an bestätigte Freunde möglich.";
                }
                return;
            }
            int longest = candidates.Max(c => c.Friend.CharacterName.Length + (input.StartsWith(c.Friend.CharacterName + "@", StringComparison.OrdinalIgnoreCase) ? c.Friend.HomeWorld.Length + 1 : 0));
            var best = candidates.Where(c => c.Friend.CharacterName.Length + (input.StartsWith(c.Friend.CharacterName + "@", StringComparison.OrdinalIgnoreCase) ? c.Friend.HomeWorld.Length + 1 : 0) == longest).ToList();
            if (best.Count != 1) { lock (gate) pnStatus = "Name nicht eindeutig – bitte @Heimatwelt ergänzen."; return; }
            var target = best[0];
            if (target.Text.Length < 1 || target.Text.Length > 1500) { lock (gate) pnStatus = "Nachricht muss 1 bis 1500 Zeichen enthalten."; return; }
            await ConnectRequestAsync("message_send", HttpMethod.Post,
                new { userId = target.Friend.OtherUserId, body = target.Text }, authenticate: true);
            lock (gate)
            {
                pnStatus = "Nachricht an " + target.Friend.CharacterName + "@" + target.Friend.HomeWorld + " gesendet.";
                pendingChatNotices.Enqueue(pnStatus);
            }
        }
        catch (Exception ex)
        {
            lock (gate) { pnStatus = "Nachricht fehlgeschlagen: " + ex.Message; pendingChatNotices.Enqueue(pnStatus); }
        }
    }

    private async Task LoadWorldUsersAsync(bool more)
    {
        lock (gate) { if (socialBusy) return; socialBusy = true; worldStatus = "Lade Weltspieler ..."; }
        try
        {
            var offset = more ? (worldNextOffset ?? 0) : 0;
            var response = await ConnectRequestAsync("users_world", HttpMethod.Post,
                new { offset }, authenticate: true);
            using var doc = JsonDocument.Parse(response.Json);
            var users = JsonSerializer.Deserialize<List<UserItem>>(doc.RootElement.GetProperty("users").GetRawText(), SocialJson) ?? [];
            int? next = doc.RootElement.GetProperty("nextOffset").ValueKind == JsonValueKind.Null
                ? null : doc.RootElement.GetProperty("nextOffset").GetInt32();
            lock (gate)
            {
                if (!more) worldUsers = users;
                else worldUsers.AddRange(users);
                worldNextOffset = next;
                worldStatus = $"{worldUsers.Count} registrierte Spieler auf {registeredWorld} geladen.";
            }
        }
        catch (Exception ex) { lock (gate) worldStatus = "Weltensuche fehlgeschlagen: " + ex.Message; }
        finally { lock (gate) socialBusy = false; }
    }

    private async Task LoadInboxAsync()
    {
        lock (gate) { if (inboxBusy) return; inboxBusy = true; }
        try
        {
            var response = await ConnectRequestAsync("inbox", HttpMethod.Post, authenticate: true);
            using var doc = JsonDocument.Parse(response.Json);
            var data = JsonSerializer.Deserialize<List<InboxItem>>(doc.RootElement.GetProperty("messages").GetRawText(), SocialJson) ?? [];
            lock (gate)
            {
                unreadMessages = data;
                inboxStatus = data.Count + " ungelesene Nachrichten.";
                foreach (var item in data)
                {
                    if (alertedMessageIds.Add(item.Id))
                        pendingChatNotices.Enqueue("Neue private Nachricht von " + item.CharacterName + "@" + item.HomeWorld + " – /aetherfluss öffnen.");
                }
                if (alertedMessageIds.Count > 2000) alertedMessageIds.Clear();
            }
        }
        catch (Exception ex) { lock (gate) inboxStatus = "Posteingang: " + ex.Message; }
        finally { lock (gate) { inboxBusy = false; nextInboxCheckUtc = DateTime.UtcNow.AddSeconds(30); } }
    }

    public sealed class InboxItem : ChatItem
    {
        public string CharacterName { get; set; } = "";
        public string HomeWorld { get; set; } = "";
    }

    private async Task LoadDirectorySettingsAsync()
    {
        try
        {
            var response = await ConnectRequestAsync("directory_settings", HttpMethod.Post, authenticate: true);
            using var doc = JsonDocument.Parse(response.Json);
            lock (gate)
            {
                directoryVisible = doc.RootElement.GetProperty("visible").GetBoolean();
                directorySettingsLoaded = true;
            }
        }
        catch (Exception ex) { lock (gate) directoryStatus = "Verzeichniseinstellungen: " + ex.Message; }
    }

    private async Task UpdateDirectoryVisibilityAsync(bool visible)
    {
        try
        {
            var response = await ConnectRequestAsync("directory_settings", HttpMethod.Post,
                new { visible }, authenticate: true);
            using var doc = JsonDocument.Parse(response.Json);
            lock (gate)
            {
                directoryVisible = doc.RootElement.GetProperty("visible").GetBoolean();
                directoryStatus = directoryVisible ? "Dein Charakter ist in der Suche sichtbar." : "Dein Charakter ist in der Suche verborgen.";
            }
        }
        catch (Exception ex) { lock (gate) directoryStatus = "Sichtbarkeit konnte nicht gespeichert werden: " + ex.Message; }
    }

    private async Task LoadDirectoryWorldsAsync()
    {
        try
        {
            var response = await ConnectRequestAsync("directory_worlds", HttpMethod.Post, authenticate: true);
            using var doc = JsonDocument.Parse(response.Json);
            var worlds = JsonSerializer.Deserialize<List<string>>(doc.RootElement.GetProperty("worlds").GetRawText()) ?? [];
            lock (gate) availableWorlds = worlds;
        }
        catch (Exception ex) { lock (gate) directoryStatus = "Weltliste konnte nicht geladen werden: " + ex.Message; }
    }

    private async Task SearchDirectoryAsync(bool more)
    {
        if (!HasMatchingConnectCharacter()) return;
        string world, name;
        int offset;
        lock (gate)
        {
            if (directoryBusy) return;
            directoryBusy = true;
            directoryStatus = "Suche Connect-Kontakte ...";
            world = directoryWorld;
            name = directoryQuery.Trim();
            offset = more ? directoryNextOffset ?? 0 : 0;
        }
        try
        {
            var response = await ConnectRequestAsync("users_directory", HttpMethod.Post,
                new { world, query = name, offset }, authenticate: true);
            using var doc = JsonDocument.Parse(response.Json);
            var rows = JsonSerializer.Deserialize<List<UserItem>>(doc.RootElement.GetProperty("users").GetRawText(), SocialJson) ?? [];
            int? next = doc.RootElement.GetProperty("nextOffset").ValueKind == JsonValueKind.Null
                ? null : doc.RootElement.GetProperty("nextOffset").GetInt32();
            lock (gate)
            {
                if (!more) directoryUsers = rows;
                else directoryUsers.AddRange(rows);
                directoryNextOffset = next;
                directoryStatus = directoryUsers.Count + " sichtbare Connect-Charaktere gefunden.";
            }
        }
        catch (Exception ex) { lock (gate) directoryStatus = "Kontaktsuche: " + ex.Message; }
        finally { lock (gate) directoryBusy = false; }
    }

    private string? ResolveUniqueFriendName(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        lock (gate)
        {
            var found = friendItems.Where(f => f.Status == "accepted")
                .Select(f => f.CharacterName + "@" + f.HomeWorld)
                .Where(n => n.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            return found.Length == 1 ? found[0] : null;
        }
    }

    private string? ResolveUniqueDirectoryName(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        lock (gate)
        {
            var result = directoryUsers.Select(u => u.CharacterName + "@" + u.HomeWorld)
                .Concat(worldUsers.Select(u => u.CharacterName + "@" + u.HomeWorld))
                .Concat(friendItems.Select(f => f.CharacterName + "@" + f.HomeWorld))
                .Where(n => n.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            return result.Length == 1 ? result[0] : null;
        }
    }

    private void DrawDirectory()
    {
        if (!directorySettingsLoaded && ImGui.SmallButton("Verzeichniseinstellungen laden"))
            _ = LoadDirectorySettingsAsync();
        if (directorySettingsLoaded)
        {
            bool visible = directoryVisible;
            if (ImGui.Checkbox("Mich für andere Connect-Nutzer sichtbar machen", ref visible))
                _ = UpdateDirectoryVisibilityAsync(visible);
        }
        ImGui.TextDisabled("Nur freiwillig sichtbare Connect-Nutzer. Kein FFXIV-Online-Status oder Spieler-Scan.");
        if (ImGui.SmallButton("Verfügbare Welten laden")) _ = LoadDirectoryWorldsAsync();
        ImGui.SameLine();
        if (ImGui.SmallButton("Alle Welten auswählen")) directoryWorld = "";
        string selectedWorld = string.IsNullOrEmpty(directoryWorld) ? "Alle Welten" : directoryWorld;
        if (ImGui.BeginCombo("Heimatwelt##directory", selectedWorld))
        {
            if (ImGui.Selectable("Alle Welten##directoryall")) directoryWorld = "";
            List<string> worlds;
            lock (gate) worlds = [.. availableWorlds];
            foreach (var world in worlds)
                if (ImGui.Selectable(world + "##directoryworld")) directoryWorld = world;
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(280);
        ImGui.InputTextWithHint("##directoryname", "Charaktername (optional, ab 2 Zeichen)", ref directoryQuery, 64);
        ImGui.SameLine();
        if (ImGui.Button("Kontakte suchen") && (directoryQuery.Trim().Length == 0 || directoryQuery.Trim().Length >= 2))
            _ = SearchDirectoryAsync(false);
        ImGui.TextWrapped(directoryStatus);
        List<UserItem> items;
        int? next;
        lock (gate) { items = [.. directoryUsers]; next = directoryNextOffset; }
        foreach (var user in items)
        {
            ImGui.PushID("directory" + user.Id);
            ImGui.TextUnformatted(user.CharacterName + "@" + user.HomeWorld);
            ImGui.SameLine();
            if (ImGui.SmallButton("Kontaktanfrage"))
                _ = SocialOperationAsync("friend_request", HttpMethod.Post, new { userId = user.Id });
            ImGui.PopID();
        }
        if (next.HasValue && ImGui.Button("Weitere Kontakte laden")) _ = SearchDirectoryAsync(true);
    }

    // Vorschläge erfolgen lokal aus bereits geladenen Daten; kein zusätzlicher Request pro Tastendruck.
    private void DrawPnComposer()
    {
        if (!showPnComposer) return;
        if (!ImGui.CollapsingHeader("Private Nachricht verfassen", ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.TextDisabled("Nur bestätigte Freunde; eindeutige Namen mit @Heimatwelt.");
        ImGui.SetNextItemWidth(350);
        bool pnTab = false;
        ImGui.InputTextWithHint("##pnrecipient", "Charaktername oder Name@Welt", ref pnRecipient, 120,
            ImGuiInputTextFlags.CallbackCompletion, (ref ImGuiInputTextCallbackData data) =>
            {
                pnTab = true;
                return 0;
            });
        if (pnTab)
        {
            var match = ResolveUniqueFriendName(pnRecipient);
            if (match is not null) pnRecipient = match;
            else pnStatus = "Tab: Bitte mehr Zeichen eingeben oder Kontakt auswählen.";
        }
        ImGui.TextDisabled("Tab ergänzt einen eindeutig erkannten Freund.");
        List<FriendItem> choices;
        lock (gate)
        {
            choices = friendItems.Where(f => f.Status == "accepted" &&
                (string.IsNullOrWhiteSpace(pnRecipient) ||
                 (f.CharacterName + "@" + f.HomeWorld).Contains(pnRecipient.Trim(), StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f.CharacterName).ThenBy(f => f.HomeWorld).Take(8).ToList();
        }
        foreach (var friend in choices)
        {
            var display = friend.CharacterName + "@" + friend.HomeWorld;
            if (ImGui.Selectable(display + "##pnfriend" + friend.OtherUserId)) pnRecipient = display;
        }
        ImGui.SetNextItemWidth(-110);
        ImGui.InputTextWithHint("##pncompose", "Nachricht (max. 1500 Zeichen)", ref pnComposeText, 1500);
        ImGui.SameLine();
        if (ImGui.Button("PN senden") && pnComposeText.Trim().Length is > 0 and <= 1500)
        {
            // Nur aufgelöste Freunde wählen; die bestehende Sendefunktion prüft erneut serverseitig.
            var recipient = pnRecipient.Trim();
            FriendItem? match;
            lock (gate)
            {
                var exact = friendItems.Where(f => f.Status == "accepted" &&
                    (string.Equals(f.CharacterName + "@" + f.HomeWorld, recipient, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(f.CharacterName, recipient, StringComparison.OrdinalIgnoreCase))).Take(2).ToList();
                match = exact.Count == 1 ? exact[0] : null;
            }
            if (match is null) pnStatus = "Bitte einen eindeutigen Freund aus den Vorschlägen auswählen.";
            else
            {
                var text = pnComposeText.Trim();
                pnComposeText = "";
                pnRecipient = match.CharacterName + "@" + match.HomeWorld;
                _ = SendPrivateCommandAsync(pnRecipient + " " + text);
            }
        }
    }

    private void DrawSocial()
    {
        ImGui.Spacing();
        ImGui.TextColored(GuideBlue, "ÄTHERFLUSS CONNECT");
        ImGui.TextDisabled("Nachrichten, Kontakte und Community in einem Bereich");
        if (!ImGui.BeginTabBar("##connect-workspace")) return;
        if (ImGui.BeginTabItem("Nachrichten"))
        {
            DrawConversationPicker();
        ImGui.TextWrapped("/pn Vorname Nachname@Welt Nachricht – auch während Instanzen, weltübergreifend.");
        if (!string.IsNullOrEmpty(pnStatus)) ImGui.TextWrapped(pnStatus);
        if (ImGui.SmallButton("Neue PN (/pn)"))
        {
            showPnComposer = true;
            _ = SocialOperationAsync("friends", HttpMethod.Get);
        }
        DrawPnComposer();
        ImGui.TextWrapped(inboxStatus);
        if (ImGui.SmallButton("Posteingang aktualisieren")) _ = LoadInboxAsync();
        List<InboxItem> unread;
        lock (gate) unread = [.. unreadMessages];
        if (ImGui.CollapsingHeader($"Offline-Nachrichten ({unread.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var entry in unread)
            {
                ImGui.PushID($"inbox{entry.Id}");
                ImGui.TextWrapped($"{entry.CharacterName}@{entry.HomeWorld}: {entry.Body}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Öffnen"))
                {
                    chatPartnerId = entry.SenderId;
                    chatPartnerName = entry.CharacterName + "@" + entry.HomeWorld;
                    _ = SocialOperationAsync("messages&userId=" + entry.SenderId, HttpMethod.Get);
                    nextInboxCheckUtc = DateTime.UtcNow.AddSeconds(2);
                }
                ImGui.PopID();
            }
        }
            DrawConnectChat();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Kontakte"))
        {
        ImGui.Separator();
        if (ImGui.CollapsingHeader("Kontakte aus anderen Welten", ImGuiTreeNodeFlags.DefaultOpen))
            DrawDirectory();
        ImGui.Separator();
        if (ImGui.Button("Connect-Spieler auf meiner Heimatwelt anzeigen")) _ = LoadWorldUsersAsync(false);
        ImGui.TextWrapped(worldStatus);
        List<UserItem> world;
        int? nextPage;
        lock (gate) { world = [.. worldUsers]; nextPage = worldNextOffset; }
        if (ImGui.CollapsingHeader($"Spieler auf {registeredWorld} ({world.Count})"))
        {
            foreach (var entry in world)
            {
                ImGui.TextUnformatted(entry.CharacterName + "@" + entry.HomeWorld);
                ImGui.SameLine();
                if (ImGui.SmallButton($"Anfrage##world{entry.Id}"))
                    _ = SocialOperationAsync("friend_request", HttpMethod.Post, new { userId = entry.Id });
            }
            if (nextPage is not null && ImGui.Button("Weitere Spieler laden")) _ = LoadWorldUsersAsync(true);
        }
        ImGui.TextUnformatted("Kontaktliste & private Nachrichten");
        ImGui.TextDisabled("Connect-PN funktioniert unabhängig von FFXIV-Instanzen und Heimatwelten.");
        ImGui.TextDisabled("Nur bestätigte Freunde können sich schreiben. Keine FFXIV-eigene Flüsterfunktion.");
        if (ImGui.Button("Freunde aktualisieren")) _ = SocialOperationAsync("friends", HttpMethod.Get);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(230);
        bool friendTab = false;
        ImGui.InputTextWithHint("##friendsearch", "Charakter suchen ...", ref friendSearch, 80,
            ImGuiInputTextFlags.CallbackCompletion, (ref ImGuiInputTextCallbackData data) =>
            {
                friendTab = true;
                return 0;
            });
        if (friendTab)
        {
            var match = ResolveUniqueDirectoryName(friendSearch);
            if (match is not null) friendSearch = match;
            else socialStatus = "Tab: Bitte mehr Zeichen eingeben oder Kontakt auswählen.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Finden") && friendSearch.Trim().Length >= 2)
            _ = SocialOperationAsync("users&q=" + Uri.EscapeDataString(friendSearch.Trim()), HttpMethod.Get);
        // Autocomplete aus geladenen Weltspielern und Freunden – serverseitige Suche weiterhin über "Finden".
        if (friendSearch.Trim().Length >= 2)
        {
            List<(string Name, string World)> suggestions;
            lock (gate)
            {
                suggestions = directoryUsers.Select(u => (Name: u.CharacterName, World: u.HomeWorld))
                    .Concat(worldUsers.Select(u => (Name: u.CharacterName, World: u.HomeWorld)))
                    .Concat(friendItems.Select(f => (Name: f.CharacterName, World: f.HomeWorld)))
                    .Where(p => (p.Name + "@" + p.World).Contains(friendSearch.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Distinct().OrderBy(p => p.Name).ThenBy(p => p.World).Take(8).ToList();
            }
            foreach (var suggestion in suggestions)
            {
                var name = suggestion.Name + "@" + suggestion.World;
                if (ImGui.Selectable(name + "##friendauto")) friendSearch = name;
            }
        }
        string message; List<FriendItem> friends; List<UserItem> users; bool busy;
        lock (gate) { message = socialStatus; friends = [.. friendItems]; users = [.. foundUsers]; busy = socialBusy; }
        ImGui.TextWrapped(message);
        if (ImGui.CollapsingHeader("Spielersuche"))
        {
            foreach (var item in users)
            {
                ImGui.TextUnformatted(item.CharacterName + "@" + item.HomeWorld);
                ImGui.SameLine();
                if (ImGui.SmallButton($"Anfrage senden##request{item.Id}") && !busy)
                    _ = SocialOperationAsync("friend_request", HttpMethod.Post, new { userId = item.Id });
            }
        }
        ImGui.Separator();
        foreach (var item in friends)
        {
            ImGui.PushID($"friend{item.Id}");
            ImGui.TextUnformatted($"{item.CharacterName}@{item.HomeWorld} — {item.Status}");
            if (item.Status == "pending" && item.Incoming)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Annehmen") && !busy)
                    _ = SocialOperationAsync("friend_respond", HttpMethod.Post, new { requestId = item.Id, decision = "accept" });
                ImGui.SameLine();
                if (ImGui.SmallButton("Ablehnen") && !busy)
                    _ = SocialOperationAsync("friend_respond", HttpMethod.Post, new { requestId = item.Id, decision = "reject" });
            }
            else if (item.Status == "accepted")
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Party anfragen"))
                    _ = SocialOperationAsync("message_send", HttpMethod.Post,
                        new { userId = item.OtherUserId, body = "Hast du Lust auf eine gemeinsame Party? (Ätherfluss Connect-Anfrage)" });
                ImGui.SameLine();
                if (ImGui.SmallButton("Chat öffnen"))
                {
                    chatPartnerId = item.OtherUserId;
                    chatPartnerName = item.CharacterName + "@" + item.HomeWorld;
                    chatItems = [];
                    chatHasMore = false;
                    _ = SocialOperationAsync("messages&userId=" + chatPartnerId, HttpMethod.Get);
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Entfernen") && !busy)
                    _ = SocialOperationAsync("friend_respond", HttpMethod.Post, new { requestId = item.Id, decision = "remove" });
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Blockieren") && !busy)
                _ = SocialOperationAsync("block", HttpMethod.Post, new { userId = item.OtherUserId });
            ImGui.PopID();
        }

            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Community & Termine"))
        {
            DrawSocialExtras();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Konto & Sicherheit"))
        {
            DrawConnectAccount();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawConnectAccount()
    {
        ImGui.Spacing();
        ImGui.TextColored(GuideBlue, "KONTO & SICHERHEIT");
        ImGui.TextUnformatted("Verbundenes Profil: " + registeredCharacter + "@" + registeredWorld);
        ImGui.Spacing();

        bool busy;
        lock (gate) busy = connectBusy;
        if (ImGui.Button("Profil aktualisieren") && !busy) _ = LoadConnectProfileAsync();
        ImGui.SameLine();
        if (ImGui.Button("Connect abmelden") && !busy) _ = LogoutConnectAsync();

        ImGui.Separator();
        ImGui.TextColored(GuideBlue, "Connect-Passwort");
        ImGui.TextWrapped("Neues Passwort einmal eingeben. Eine zweite Eingabe zur Bestätigung ist nicht erforderlich.");
        ImGui.InputText("Neues Passwort##new", ref newPassword, 128, ImGuiInputTextFlags.Password);
        if (ImGui.CollapsingHeader("Bestehendes Passwort zur Sicherheitsprüfung"))
        {
            ImGui.TextDisabled("Bei einem vorhandenen Passwort kann der Server das bisherige Passwort verlangen.");
            ImGui.InputText("Bisheriges Passwort##old", ref currentPassword, 128, ImGuiInputTextFlags.Password);
        }
        if (!busy && newPassword.Length >= 12 && ImGui.Button("Passwort speichern"))
            _ = SetConnectPasswordAsync(currentPassword, newPassword);
        ImGui.TextDisabled("Eine Anmeldung allein ersetzt keine serverseitige Passwort-Wiederherstellung.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, .49f, .49f, 1f), "KONTO DAUERHAFT LÖSCHEN");
        ImGui.TextWrapped("Achtung: Konto, Token, Nachrichten, Kontakte und Terminbezüge werden dauerhaft entfernt. Für die Löschung ist genau eine Passworteingabe erforderlich, kein weiterer Bestätigungsdialog.");
        if (ImGui.Button("Serverfunktion prüfen") && !busy) _ = ConnectHealthAsync();
        if (accountDeletionAvailable)
        {
            ImGui.InputText("Aktuelles Connect-Passwort##deleteaccount", ref deleteAccountPassword, 128, ImGuiInputTextFlags.Password);
            if (!busy && deleteAccountPassword.Length > 0 && ImGui.Button("KONTO UNWIDERRUFLICH LÖSCHEN"))
                _ = DeleteConnectAccountAsync(deleteAccountPassword);
        }
        else
        {
            ImGui.TextDisabled("Kontolöschung serverseitig nicht bestätigt; die Schaltfläche bleibt gesperrt.");
        }
    }

    private void DrawConversationPicker()
    {
        List<FriendItem> contacts;
        lock (gate) contacts = [.. friendItems.Where(f => f.Status == "accepted")];
        var label = chatPartnerId > 0 ? chatPartnerName : "Gespräch auswählen";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##connect-conversation-picker", label))
        {
            foreach (var friend in contacts)
            {
                var caption = friend.CharacterName + "@" + friend.HomeWorld;
                if (ImGui.Selectable(caption + "##conversation" + friend.OtherUserId, friend.OtherUserId == chatPartnerId))
                {
                    chatPartnerId = friend.OtherUserId;
                    chatPartnerName = caption;
                    lock (gate) { chatItems = []; chatHasMore = false; }
                    _ = SocialOperationAsync("messages&userId=" + chatPartnerId, HttpMethod.Get);
                }
            }
            ImGui.EndCombo();
        }
        if (contacts.Count == 0)
            ImGui.TextDisabled("Noch keine bestätigten Connect-Freunde vorhanden.");
    }

    // The chat pane is isolated so an empty selection cannot leave the tab stack unbalanced.
    private void DrawConnectChat()
    {
        List<ChatItem> chats;
        bool busy;
        lock (gate) { chats = [.. chatItems]; busy = socialBusy; }
        if (chatPartnerId <= 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Wähle im Tab Kontakte einen Freund und klicke auf Chat öffnen.");
            return;
        }
        ImGui.Separator();
        ImGui.TextUnformatted("Nachrichten: " + chatPartnerName);
        ImGui.SameLine();
        if (ImGui.SmallButton("Aktualisieren") && !busy)
            _ = SocialOperationAsync("messages&userId=" + chatPartnerId, HttpMethod.Get);
        if (ImGui.BeginChild("connect-chat", new Vector2(0, 300), true))
        {
            foreach (var item in chats)
                {
                    var author = item.SenderId == registeredUserId ? "Ich" : chatPartnerName;
                    var time = string.IsNullOrWhiteSpace(item.CreatedAt) ? "" : " · " + item.CreatedAt;
                    ImGui.TextColored(item.SenderId == registeredUserId ? GuideBlue : GuideMuted, author + time);
                    ImGui.TextWrapped(item.Body);
                    ImGui.Spacing();
                }
        }
        ImGui.EndChild();
        if (chatHasMore && !busy && chats.Count > 0)
        {
            long oldest = chats.Min(item => item.Id);
            if (ImGui.SmallButton("Ältere Nachrichten laden"))
                _ = SocialOperationAsync("messages&userId=" + chatPartnerId + "&beforeId=" + oldest, HttpMethod.Get);
        }
        DrawChatTranslations(chats);
        ImGui.SetNextItemWidth(-95);
        ImGui.InputText("##message", ref chatDraft, 1500);
        ImGui.SameLine();
        if (ImGui.Button("Senden") && !busy && !string.IsNullOrWhiteSpace(chatDraft))
        {
            var msg = chatDraft.Trim();
            _ = SocialOperationAsync("message_send", HttpMethod.Post, new { userId = chatPartnerId, body = msg });
        }
    }

    public sealed class FriendItem
    {
        public long Id { get; set; }
        public long OtherUserId { get; set; }
        public string CharacterName { get; set; } = "";
        public string HomeWorld { get; set; } = "";
        public string Status { get; set; } = "";
        public bool Incoming { get; set; }
    }
    public sealed class UserItem
    {
        public long Id { get; set; }
        public string CharacterName { get; set; } = "";
        public string HomeWorld { get; set; } = "";
    }
    public class ChatItem
    {
        public long Id { get; set; }
        public long SenderId { get; set; }
        public long RecipientId { get; set; }
        public string Body { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }

    private void DrawConfig()
    {
        if (!configShown) return;
        ImGui.SetNextWindowSize(new Vector2(560, 250), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Ätherfluss Einstellungen###AetherflussConfig", ref configShown))
        { ImGui.End(); return; }
        ImGui.TextUnformatted("Guide API (HTTPS)");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##api", ref apiBase, 512);
        ImGui.TextWrapped("Sicherheit: Nur HTTPS-Adressen auf der-aetherfluss.de sind erlaubt.");
        if (ImGui.Button("Speichern"))
        {
            if (IsAllowedApi(apiBase))
            {
                config.ApiUrl = apiBase;
                pluginInterface.SavePluginConfig(config);
                lock (gate) connectionStatus = "Adresse gespeichert.";
                _ = LoadCategoriesAsync();
            }
            else lock (gate) connectionStatus = "Ungültige Adresse: HTTPS und der-aetherfluss.de/index.php erforderlich.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Verbindung prüfen"))
        {
            if (IsAllowedApi(apiBase)) _ = TestConnectionAsync();
            else lock (gate) connectionStatus = "Bitte zuerst eine gültige API-Adresse eingeben.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Standardadresse")) apiBase = DefaultApiBase;
        string message;
        lock (gate) message = connectionStatus;
        ImGui.Separator();
        ImGui.TextWrapped(message);
        ImGui.End();
    }

    [Serializable]
    public sealed class Settings : IPluginConfiguration
    {
        public int Version { get; set; } = 1;
        public string ApiUrl { get; set; } = DefaultApiBase;
        public bool ConnectOptIn { get; set; } = false;
        public string EncryptedConnectToken { get; set; } = "";
        public bool MiniChatEnabled { get; set; } = false;
        public bool DeferNotificationsInCombat { get; set; } = true;
        public bool ShowChatNotifications { get; set; } = true;
        public bool SharePresence { get; set; } = false;
        public string PresenceState { get; set; } = "online";
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= Open;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        commands.RemoveHandler(Command);
        commands.RemoveHandler(PrivateMessageCommand);
        commands.RemoveHandler(MiniChatCommand);
        lifetime.Cancel();
        lock (videoImageGate)
        {
            foreach (var texture in videoThumbTextures.Values) texture.Dispose();
            videoThumbTextures.Clear();
            videoThumbLoading.Clear();
        }
        client.Dispose();
        lifetime.Dispose();
    }

    public sealed class CategoryResponse { public List<CategoryItem> Categories { get; set; } = []; }
    public sealed class CategoryItem
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
    }
    public sealed class SearchResponse { public List<SearchItem> Results { get; set; } = []; }
    public sealed class SearchItem { public int Id { get; set; } public string Title { get; set; } = ""; public string Summary { get; set; } = ""; public string Url { get; set; } = ""; }
    public sealed class EntryResponse { public EntryItem? Entry { get; set; } }
    public sealed class EntryItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Url { get; set; } = "";
        public Dictionary<string, List<string>>? Fields { get; set; }
        public List<EntrySection>? Sections { get; set; }
        public List<string>? Videos { get; set; }
        public List<VideoItem>? VideoItems { get; set; }
    }
    public sealed class VideoItem
    {
        public string Title { get; set; } = "";
        public string Source { get; set; } = "";
        public int? MediaId { get; set; }
        public string Url { get; set; } = "";
        public string MediaTitle { get; set; } = "";
        public string Description { get; set; } = "";
        public string? ThumbnailUrl { get; set; }
        public int? PreviewCoreFileId { get; set; }
        public int? VideoType { get; set; }
    }
    public sealed class EntrySection { public string Title { get; set; } = ""; public string Text { get; set; } = ""; }
}
