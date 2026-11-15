using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace AetherflussGuide;

/// <summary>V0.11 social features. Existing Guide/Login/Friend/PN routes stay in Plugin.cs.</summary>
public sealed partial class Plugin
{
    private readonly ICondition condition;
    private bool miniChatVisible;
    private DateTime nextPresenceUtc = DateTime.UtcNow.AddSeconds(10);
    private DateTime nextMiniRefreshUtc = DateTime.UtcNow.AddSeconds(12);
    private DateTime nextSocialRefreshUtc = DateTime.UtcNow.AddSeconds(15);
    private string miniDraft = "";
    private readonly Dictionary<long, string> friendPresence = new();
    private readonly Dictionary<long, ContactExtra> contactExtras = new();
    private string socialHubStatus = "Erweiterungen noch nicht geladen.";
    private bool socialExtrasBusy;
    private long editContactId;
    private string editContactGroup = "";
    private string editContactNickname = "";
    private string editContactNote = "";
    private bool editContactFavorite;
    private string raidTitle = "";
    private string raidDetails = "";
    private string raidLocalDate = DateTime.Now.AddDays(1).Date.AddHours(20).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private long raidInviteeId;
    private readonly List<RaidEventItem> raidEvents = [];
    private long guideShareRecipientId;
    private string guideShareUrl = "";
    private readonly Dictionary<long, string> translatedMessages = new();
    private bool translationBusy;
    private bool translationConsent;

    private void SocialHubTick()
    {
        if (!HasMatchingConnectCharacter()) return;
        var now = DateTime.UtcNow;
        if (config.SharePresence && now >= nextPresenceUtc)
        {
            nextPresenceUtc = now.AddSeconds(60);
            _ = SendPresenceAsync(config.PresenceState);
        }
        if ((shown || miniChatVisible) && now >= nextSocialRefreshUtc)
        {
            nextSocialRefreshUtc = now.AddSeconds(75);
            _ = LoadSocialExtrasAsync();
        }
        if (miniChatVisible && chatPartnerId > 0 && now >= nextMiniRefreshUtc)
        {
            nextMiniRefreshUtc = now.AddSeconds(15);
            _ = SocialOperationAsync("messages&userId=" + chatPartnerId, HttpMethod.Get);
        }
    }

    // A compact separate overlay, drawn even while the full Guide/Connect window is closed.
    private void DrawMiniChat()
    {
        if (!miniChatVisible || !config.MiniChatEnabled || !HasMatchingConnectCharacter()) return;
        ImGui.SetNextWindowSize(new Vector2(400, 285), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Ätherfluss · Mini-Chat###AetherMini", ref miniChatVisible)) { ImGui.End(); return; }
        List<FriendItem> friends;
        List<ChatItem> lines;
        int unread;
        lock (gate) { friends = [.. friendItems]; lines = [.. chatItems]; unread = unreadMessages.Count; }
        ImGui.TextUnformatted("Ungelesen: " + unread);
        if (ImGui.SmallButton("Kontakte aktualisieren")) _ = SocialOperationAsync("friends", HttpMethod.Get);
        ImGui.SameLine();
        if (ImGui.SmallButton("Posteingang")) _ = LoadInboxAsync();
        if (ImGui.BeginCombo("Kontakt##minifriend", chatPartnerId <= 0 ? "Bitte wählen" : chatPartnerName))
        {
            foreach (var f in friends.Where(f => f.Status == "accepted"))
                if (ImGui.Selectable(f.CharacterName + "@" + f.HomeWorld + "##m" + f.OtherUserId))
                {
                    chatPartnerId = f.OtherUserId;
                    chatPartnerName = f.CharacterName + "@" + f.HomeWorld;
                    lock (gate) chatItems = [];
                    _ = SocialOperationAsync("messages&userId=" + chatPartnerId, HttpMethod.Get);
                }
            ImGui.EndCombo();
        }
        if (ImGui.BeginChild("mini-history", new Vector2(0, 125), true))
        {
            foreach (var message in lines.TakeLast(12))
                ImGui.TextWrapped((message.SenderId == registeredUserId ? "Ich: " : chatPartnerName + ": ") + message.Body);
        }
        ImGui.EndChild();
        ImGui.SetNextItemWidth(-78);
        ImGui.InputTextWithHint("##mini-answer", "Schnellantwort ...", ref miniDraft, 1500);
        ImGui.SameLine();
        if (ImGui.Button("Senden") && chatPartnerId > 0 && miniDraft.Trim().Length > 0)
        {
            var text = miniDraft.Trim();
            miniDraft = "";
            _ = SocialOperationAsync("message_send", HttpMethod.Post, new { userId = chatPartnerId, body = text });
        }
        ImGui.End();
    }

    private async Task SendPresenceAsync(string status)
    {
        try { await ConnectRequestAsync("x_presence_set", HttpMethod.Post, new { status }, authenticate: true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) socialHubStatus = "Statusübertragung: " + ex.Message; }
    }
    private async Task LoadSocialExtrasAsync()
    {
        lock (gate) { if (socialExtrasBusy) return; socialExtrasBusy = true; socialHubStatus = "Lade Social Hub ..."; }
        try
        {
            var p = await ConnectRequestAsync("x_presence_list", HttpMethod.Post, authenticate: true);
            var m = await ConnectRequestAsync("x_contact_list", HttpMethod.Post, authenticate: true);
            var r = await ConnectRequestAsync("x_event_list", HttpMethod.Post, authenticate: true);
            using var pd = JsonDocument.Parse(p.Json);
            using var md = JsonDocument.Parse(m.Json);
            using var rd = JsonDocument.Parse(r.Json);
            var people = JsonSerializer.Deserialize<List<PresenceItem>>(pd.RootElement.GetProperty("users").GetRawText(), SocialJson) ?? [];
            var meta = JsonSerializer.Deserialize<List<ContactExtra>>(md.RootElement.GetProperty("contacts").GetRawText(), SocialJson) ?? [];
            var events = JsonSerializer.Deserialize<List<RaidEventItem>>(rd.RootElement.GetProperty("events").GetRawText(), SocialJson) ?? [];
            lock (gate)
            {
                friendPresence.Clear();
                foreach (var person in people) friendPresence[person.UserId] = person.Status;
                contactExtras.Clear();
                foreach (var item in meta) contactExtras[item.UserId] = item;
                raidEvents.Clear(); raidEvents.AddRange(events);
                socialHubStatus = $"{people.Count} Statuswerte, {meta.Count} Kontaktprofile, {events.Count} Termine geladen.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) socialHubStatus = "Social Hub: " + ex.Message; }
        finally { lock (gate) socialExtrasBusy = false; }
    }
    private async Task SaveContactExtraAsync()
    {
        try
        {
            await ConnectRequestAsync("x_contact_save", HttpMethod.Post, new
            {
                userId = editContactId,
                group = editContactGroup.Trim(), nickname = editContactNickname.Trim(),
                note = editContactNote.Trim(), favorite = editContactFavorite
            }, authenticate: true);
            lock (gate) socialHubStatus = "Kontakt gespeichert.";
            await LoadSocialExtrasAsync();
        }
        catch (Exception ex) { lock (gate) socialHubStatus = "Kontaktfehler: " + ex.Message; }
    }
    private async Task CreateRaidAsync()
    {
        try
        {
            if (!DateTime.TryParseExact(raidLocalDate, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var localTime))
            { lock (gate) socialHubStatus = "Termin bitte als JJJJ-MM-TT HH:mm eingeben."; return; }
            var utc = DateTime.SpecifyKind(localTime, DateTimeKind.Local).ToUniversalTime();
            var invitees = raidInviteeId > 0 ? new long[] { raidInviteeId } : Array.Empty<long>();
            await ConnectRequestAsync("x_event_create", HttpMethod.Post, new
            {
                title = raidTitle.Trim(), details = raidDetails.Trim(), startsAt = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                invitees, guideId = 0
            }, authenticate: true);
            raidTitle = ""; raidDetails = "";
            await LoadSocialExtrasAsync();
        }
        catch (Exception ex) { lock (gate) socialHubStatus = "Terminfehler: " + ex.Message; }
    }
    private async Task RsvpAsync(long eventId, string rsvp)
    {
        try
        {
            await ConnectRequestAsync("x_event_rsvp", HttpMethod.Post, new { eventId, rsvp }, authenticate: true);
            await LoadSocialExtrasAsync();
        }
        catch (Exception ex) { lock (gate) socialHubStatus = "Rückmeldung: " + ex.Message; }
    }
    private async Task TranslateAsync(long id)
    {
        if (!translationConsent || translationBusy) return;
        translationBusy = true;
        try
        {
            var reply = await ConnectRequestAsync("x_translate_message", HttpMethod.Post,
                new { messageId = id, target = "DE" }, authenticate: true);
            using var doc = JsonDocument.Parse(reply.Json);
            var result = doc.RootElement.GetProperty("text").GetString() ?? "";
            lock (gate) translatedMessages[id] = result;
        }
        catch (Exception ex) { lock (gate) socialHubStatus = "Übersetzung: " + ex.Message; }
        finally { translationBusy = false; }
    }
    private void StartEditContact(FriendItem f)
    {
        editContactId = f.OtherUserId;
        ContactExtra? meta;
        lock (gate) contactExtras.TryGetValue(f.OtherUserId, out meta);
        editContactGroup = meta?.Group ?? "";
        editContactNickname = meta?.Nickname ?? "";
        editContactNote = meta?.Note ?? "";
        editContactFavorite = meta?.Favorite ?? false;
    }

    private void DrawSocialExtras()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Social Hub · V0.11");
        if (ImGui.SmallButton("Social Hub laden")) _ = LoadSocialExtrasAsync();
        ImGui.SameLine();
        if (ImGui.SmallButton(config.MiniChatEnabled ? "Mini-Chat öffnen" : "Mini-Chat aktivieren"))
        {
            config.MiniChatEnabled = true;
            miniChatVisible = true;
            pluginInterface.SavePluginConfig(config);
        }
        ImGui.TextDisabled(socialHubStatus);
        if (ImGui.CollapsingHeader("Online-Status & Benachrichtigungen"))
        {
            bool share = config.SharePresence;
            if (ImGui.Checkbox("Status freiwillig mit Freunden teilen", ref share))
            {
                config.SharePresence = share;
                pluginInterface.SavePluginConfig(config);
                _ = SendPresenceAsync(share ? config.PresenceState : "invisible");
            }
            if (share)
            {
                if (ImGui.BeginCombo("Mein Connect-Status", config.PresenceState))
                {
                    foreach (var option in new[] { "online", "away", "busy", "instance", "invisible" })
                        if (ImGui.Selectable(option)) { config.PresenceState = option; nextPresenceUtc = DateTime.UtcNow; pluginInterface.SavePluginConfig(config); }
                    ImGui.EndCombo();
                }
            }
            bool defer = config.DeferNotificationsInCombat;
            if (ImGui.Checkbox("Hinweise während Kämpfen zurückhalten", ref defer))
            { config.DeferNotificationsInCombat = defer; pluginInterface.SavePluginConfig(config); }
            bool notices = config.ShowChatNotifications;
            if (ImGui.Checkbox("Hinweise im FFXIV-Chat erlauben", ref notices))
            { config.ShowChatNotifications = notices; pluginInterface.SavePluginConfig(config); }
            ImGui.TextDisabled("Nur freiwilliger Connect-Status, keine Standort- oder Gruppenüberwachung.");
        }
        if (ImGui.CollapsingHeader("Kontaktgruppen, Favoriten und persönliche Notizen"))
        {
            List<FriendItem> friends;
            Dictionary<long, ContactExtra> metaCopy;
            lock (gate) { friends = [.. friendItems.Where(f => f.Status == "accepted")]; metaCopy = new(contactExtras); }
            foreach (var friend in friends.OrderByDescending(f => metaCopy.TryGetValue(f.OtherUserId, out var e) && e.Favorite)
                         .ThenBy(f => metaCopy.TryGetValue(f.OtherUserId, out var e) ? e.Group : "")
                         .ThenBy(f => f.CharacterName))
            {
                ContactExtra? extra;
                string presence;
                lock (gate) { contactExtras.TryGetValue(friend.OtherUserId, out extra); presence = friendPresence.GetValueOrDefault(friend.OtherUserId, "offline"); }
                string nick = string.IsNullOrWhiteSpace(extra?.Nickname) ? friend.CharacterName : extra!.Nickname;
                ImGui.TextUnformatted((extra?.Favorite == true ? "★ " : "") + nick + "@" + friend.HomeWorld +
                    (string.IsNullOrEmpty(extra?.Group) ? "" : " [" + extra.Group + "]") + " · " + presence);
                ImGui.SameLine();
                if (ImGui.SmallButton("Bearbeiten##contact" + friend.OtherUserId)) StartEditContact(friend);
            }
            if (editContactId > 0)
            {
                ImGui.Separator();
                ImGui.TextUnformatted("Kontakt-ID " + editContactId);
                ImGui.InputText("Gruppe##contactmeta", ref editContactGroup, 50);
                ImGui.InputText("Spitzname##contactmeta", ref editContactNickname, 60);
                ImGui.InputTextMultiline("Private Notiz##contactmeta", ref editContactNote, 500, new Vector2(-1, 55));
                ImGui.Checkbox("Favorit##contactmeta", ref editContactFavorite);
                if (ImGui.Button("Kontaktdaten speichern")) _ = SaveContactExtraAsync();
            }
        }
        if (ImGui.CollapsingHeader("Raid-Verabredungen"))
        {
            ImGui.InputText("Raid / Titel", ref raidTitle, 120);
            ImGui.InputText("Datum (lokal, JJJJ-MM-TT HH:mm)", ref raidLocalDate, 16);
            ImGui.InputText("Beschreibung", ref raidDetails, 500);
            List<FriendItem> friends;
            lock (gate) friends = [.. friendItems.Where(f => f.Status == "accepted")];
            string target = friends.FirstOrDefault(f => f.OtherUserId == raidInviteeId) is { } targetFriend
                ? targetFriend.CharacterName + "@" + targetFriend.HomeWorld : "Nur für mich";
            if (ImGui.BeginCombo("Freund einladen", target))
            {
                if (ImGui.Selectable("Keine Einladung")) raidInviteeId = 0;
                foreach (var friend in friends)
                    if (ImGui.Selectable(friend.CharacterName + "@" + friend.HomeWorld + "##raid" + friend.OtherUserId))
                        raidInviteeId = friend.OtherUserId;
                ImGui.EndCombo();
            }
            if (raidTitle.Trim().Length >= 3 && ImGui.Button("Termin erstellen")) _ = CreateRaidAsync();
            List<RaidEventItem> events;
            lock (gate) events = [.. raidEvents];
            foreach (var item in events)
            {
                ImGui.PushID("event" + item.Id);
                ImGui.TextWrapped(item.Title + " · " + item.StartsAt + " UTC" + " · " + item.Rsvp);
                if (!string.IsNullOrEmpty(item.Details)) ImGui.TextDisabled(item.Details);
                if (item.CreatorId != registeredUserId)
                {
                    if (ImGui.SmallButton("Zusagen")) _ = RsvpAsync(item.Id, "yes");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Absagen")) _ = RsvpAsync(item.Id, "no");
                }
                ImGui.PopID();
            }
        }
        if (ImGui.CollapsingHeader("Web-Messenger & Übersetzung"))
        {
            ImGui.TextWrapped("Den gleichen Chat über den Browser verwenden, wenn FFXIV nicht läuft.");
            if (ImGui.Button("Web-Messenger öffnen")) Dalamud.Utility.Util.OpenLink("https://der-aetherfluss.de/aetherfluss-api/web-connect.php");
            ImGui.TextDisabled("Manuelle Übersetzung nur mit aktivem externem DeepL-Dienst; Nachrichten werden dann an DeepL übermittelt.");
            ImGui.Checkbox("Ich stimme für einzelne Übersetzungen zu", ref translationConsent);
        }
    }

    private void DrawGuideShare(EntryItem entry)
    {
        if (!HasMatchingConnectCharacter()) return;
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
            !parsed.Host.Equals("der-aetherfluss.de", StringComparison.OrdinalIgnoreCase)) return;
        ImGui.Separator();
        ImGui.TextUnformatted("Guide mit einem Connect-Freund teilen");
        List<FriendItem> friends;
        lock (gate) friends = [.. friendItems.Where(f => f.Status == "accepted")];
        var display = friends.FirstOrDefault(f => f.OtherUserId == guideShareRecipientId);
        if (ImGui.BeginCombo("Empfänger##guideshare", display is null ? "Freund auswählen" : display.CharacterName + "@" + display.HomeWorld))
        {
            foreach (var f in friends)
                if (ImGui.Selectable(f.CharacterName + "@" + f.HomeWorld + "##g" + f.OtherUserId)) guideShareRecipientId = f.OtherUserId;
            ImGui.EndCombo();
        }
        if (guideShareRecipientId > 0 && ImGui.SmallButton("Guide senden"))
        {
            _ = SocialOperationAsync("message_send", HttpMethod.Post,
                new { userId = guideShareRecipientId, body = "Ätherfluss-Guide: " + entry.Title + " " + parsed.AbsoluteUri });
        }
    }

    private void DrawChatTranslations(List<ChatItem> chats)
    {
        if (!translationConsent) return;
        foreach (var message in chats.TakeLast(10))
        {
            ImGui.PushID("translate" + message.Id);
            var preview = message.Body.Length <= 50 ? message.Body : message.Body[..50] + "…";
            ImGui.TextWrapped("Nr. " + message.Id + " · " + preview);
            if (ImGui.SmallButton("Auf Deutsch übersetzen")) _ = TranslateAsync(message.Id);
            if (translatedMessages.TryGetValue(message.Id, out var text)) ImGui.TextWrapped("Übersetzung: " + text);
            ImGui.PopID();
        }
    }
    public sealed class PresenceItem { public long UserId { get; set; } public string Status { get; set; } = "offline"; }
    public sealed class ContactExtra
    {
        public long UserId { get; set; }
        public string Group { get; set; } = "";
        public string Nickname { get; set; } = "";
        public string Note { get; set; } = "";
        public bool Favorite { get; set; }
    }
    public sealed class RaidEventItem
    {
        public long Id { get; set; }
        public long CreatorId { get; set; }
        public string Title { get; set; } = "";
        public string StartsAt { get; set; } = "";
        public string Details { get; set; } = "";
        public string Rsvp { get; set; } = "pending";
    }
}
