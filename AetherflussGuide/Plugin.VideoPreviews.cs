using System.Numerics;
using System.Net;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace AetherflussGuide;

public sealed partial class Plugin
{
    private bool videoPreviewsAllowed;
    private readonly object videoImageGate = new();
    private readonly Dictionary<string, IDalamudTextureWrap> videoThumbTextures = new();
    private readonly HashSet<string> videoThumbLoading = new();
    private readonly HashSet<string> videoThumbFailed = new();

    // Only use the validated video identifier supplied by the trusted EasyMedia/Guide API.
    private static string? YoutubeId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") return null;
        string? id = null;
        if (u.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            id = u.AbsolutePath.Trim('/').Split('/')[0];
        else if (u.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
              || u.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            if (u.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in u.Query.TrimStart('?').Split('&'))
                    if (part.StartsWith("v=", StringComparison.Ordinal)) { id = Uri.UnescapeDataString(part[2..]); break; }
            }
            else if (u.AbsolutePath.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase))
                id = u.AbsolutePath[7..].Split('/')[0];
        }
        return id != null && Regex.IsMatch(id, "^[a-zA-Z0-9_-]{11}$") ? id : null;
    }

    private void DrawVideoThumbnail(string url, Vector2 size)
    {
        var id = YoutubeId(url);
        var pos = ImGui.GetCursorScreenPos();
        if (id == null || !videoPreviewsAllowed)
        {
            DrawVideoPlaceholder(pos, size, id == null ? "Kein Bild" : "▶ Vorschau");
            ImGui.Dummy(size);
            return;
        }

        IDalamudTextureWrap? wrap;
        bool waiting, failed;
        lock (videoImageGate)
        {
            videoThumbTextures.TryGetValue(id, out wrap);
            waiting = videoThumbLoading.Contains(id);
            failed = videoThumbFailed.Contains(id);
            if (wrap == null && !waiting && !failed && !lifetime.IsCancellationRequested)
            {
                videoThumbLoading.Add(id);
                _ = LoadVideoThumbnailAsync(id);
            }
        }
        if (wrap != null)
            ImGui.Image(wrap.Handle, size);
        else
        {
            DrawVideoPlaceholder(pos, size, failed ? "Vorschau nicht verfügbar" : "Lade Vorschau …");
            ImGui.Dummy(size);
        }
    }

    private static void DrawVideoPlaceholder(Vector2 pos, Vector2 size, string caption)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(new Vector4(.08f, .12f, .19f, 1)), 7f);
        draw.AddRect(pos, pos + size, ImGui.GetColorU32(new Vector4(.2f, .3f, .42f, 1)), 7f);
        draw.AddText(pos + new Vector2(8f, size.Y / 2 - 8), ImGui.GetColorU32(new Vector4(.7f, .83f, .95f, 1)), caption);
    }

    private async Task LoadVideoThumbnailAsync(string id)
    {
        IDalamudTextureWrap? texture = null;
        try
        {
            // YouTube preview images only, never arbitrary URLs from remote articles.
            var uri = new Uri($"https://i.ytimg.com/vi/{id}/hqdefault.jpg");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 2_000_000) throw new InvalidOperationException("Bild zu groß");
            var bytes = await response.Content.ReadAsByteArrayAsync(lifetime.Token);
            if (bytes.Length > 2_000_000 || bytes.Length == 0) throw new InvalidOperationException("Ungültiges Bild");
            texture = await textures.CreateFromImageAsync(bytes, $"Aetherfluss YT {id}", lifetime.Token);
            lock (videoImageGate)
            {
                if (!lifetime.IsCancellationRequested)
                {
                    videoThumbTextures[id] = texture;
                    texture = null;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            lock (videoImageGate) videoThumbFailed.Add(id);
        }
        finally
        {
            texture?.Dispose();
            lock (videoImageGate) videoThumbLoading.Remove(id);
        }
    }
}
