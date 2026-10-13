using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Zhehao.Toolkit.Runtime
{
    public sealed class SignedManifest
    {
        public string Version { get; set; }
        public string FileName { get; set; }
        public string DownloadUrl { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string Notes { get; set; }
    }

    public static class GitHubUpdates
    {
        public const string Repository = "__REPOSITORY__";
        public const string PublicKey = "__PUBLIC_KEY__";
        public const string SigningKeyId = "__SIGNING_KEY_ID__";
        public const string Product = "喆好工具箱";
        private const string ManifestName = "zhehao-update.json";
        private const int ManifestLimit = 32768;
        private static readonly Dictionary<string, string> MemoryManifests = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly object CacheGate = new object();

        public static string NormalizeVersion(string value)
        {
            if (value == null) throw new InvalidDataException("更新版本缺失。");
            if (value.StartsWith("v", StringComparison.Ordinal)) value = value.Substring(1);
            Version parsed;
            if (!Version.TryParse(value, out parsed) || parsed.Build < 0 || parsed.Major < 0 || parsed.Minor < 0)
                throw new InvalidDataException("更新版本无效。");
            return parsed.Revision <= 0 ? parsed.ToString(3) : parsed.ToString(4);
        }

        public static SignedManifest VerifyManifest(string signedJson)
        {
            if (signedJson == null || Encoding.UTF8.GetByteCount(signedJson) > ManifestLimit)
                throw new InvalidDataException("更新清单过大或缺失。");
            using (JsonDocument envelope = JsonDocument.Parse(signedJson))
            {
                byte[] payload = Convert.FromBase64String(envelope.RootElement.GetProperty("payload").GetString());
                byte[] signature = Convert.FromBase64String(envelope.RootElement.GetProperty("signature").GetString());
                if (payload.Length > 16384 || signature.Length != 384) throw new CryptographicException("更新清单签名格式无效。");
                using (RSA rsa = RSA.Create())
                {
                    int consumed;
                    rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out consumed);
                    if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                        throw new CryptographicException("更新清单签名校验失败。");
                }
                using (JsonDocument document = JsonDocument.Parse(payload))
                {
                    JsonElement root = document.RootElement;
                    string version = NormalizeVersion(root.GetProperty("version").GetString());
                    string fileName = "Zhehao.Toolkit.v" + version + ".exe";
                    string downloadUrl = "https://github.com/" + Repository + "/releases/download/v" + version + "/" + fileName;
                    string sha = root.GetProperty("sha256").GetString();
                    long size = root.GetProperty("size").GetInt64();
                    if (root.GetProperty("format").GetInt32() != 1 || root.GetProperty("product").GetString() != Product ||
                        root.GetProperty("repository").GetString() != Repository || root.GetProperty("file_name").GetString() != fileName ||
                        root.GetProperty("download_url").GetString() != downloadUrl || size <= 0 || size > 1073741824L ||
                        sha == null || sha.Length != 64)
                        throw new CryptographicException("更新清单的产品、版本或下载地址无效。");
                    foreach (char digit in sha) if (!Uri.IsHexDigit(digit)) throw new CryptographicException("更新哈希格式无效。");
                    JsonElement notes;
                    string releaseNotes = root.TryGetProperty("notes", out notes) && notes.ValueKind == JsonValueKind.String ? notes.GetString() : "";
                    return new SignedManifest { Version = version, FileName = fileName, DownloadUrl = downloadUrl, Sha256 = sha.ToUpperInvariant(), Size = size, Notes = releaseNotes };
                }
            }
        }

        public static T ParseRelease<T>(string releaseJson, string signedManifest, string currentVersion)
        {
            SignedManifest manifest = VerifyManifest(signedManifest);
            using (JsonDocument document = JsonDocument.Parse(releaseJson))
            {
                JsonElement root = document.RootElement;
                if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                    throw new InvalidDataException("更新发布状态无效。");
                if (NormalizeVersion(root.GetProperty("tag_name").GetString()) != manifest.Version)
                    throw new CryptographicException("发布版本与签名清单不一致。");
                string releaseUrl = "https://github.com/" + Repository + "/releases/tag/v" + manifest.Version;
                if (root.GetProperty("html_url").GetString() != releaseUrl) throw new CryptographicException("更新仓库地址不一致。");
                bool assetFound = false;
                foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() != manifest.FileName) continue;
                    if (assetFound || asset.GetProperty("browser_download_url").GetString() != manifest.DownloadUrl ||
                        asset.GetProperty("size").GetInt64() != manifest.Size)
                        throw new CryptographicException("发布文件与签名清单不一致。");
                    JsonElement digest;
                    if (asset.TryGetProperty("digest", out digest) && digest.ValueKind == JsonValueKind.String &&
                        !string.Equals(digest.GetString(), "sha256:" + manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new CryptographicException("GitHub 文件摘要与签名清单不一致。");
                    assetFound = true;
                }
                if (!assetFound) throw new InvalidDataException("发布中缺少工具箱 EXE。");
                if (new Version(manifest.Version).CompareTo(new Version(NormalizeVersion(currentVersion))) <= 0) return default(T);
                JsonElement notes;
                string body = root.TryGetProperty("body", out notes) && notes.ValueKind == JsonValueKind.String ? notes.GetString() : "";
                return (T)Activator.CreateInstance(typeof(T), new object[] { manifest.Version, body, new Uri(manifest.DownloadUrl), new Uri(releaseUrl), (long?)manifest.Size, manifest.Sha256 });
            }
        }

        private static HttpClient CreateClient()
        {
            HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZhehaoToolkit/1.0");
            return client;
        }

        private static bool IsDownloadHost(Uri uri)
        {
            return uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
                (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<string> ReadSmallResponseAsync(HttpResponseMessage response, int maximum, CancellationToken token)
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("更新响应过大。");
            using (Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + read > maximum) throw new InvalidDataException("更新响应过大。");
                    output.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }

        private static async Task<string> FetchManifestAsync(string version, CancellationToken token)
        {
            // GitHub's public latest-asset route does not consume the anonymous REST API quota.
            if (version != null) version = NormalizeVersion(version);
            string route = version == null ? "latest/download/" : "download/v" + version + "/";
            Uri uri = new Uri("https://github.com/" + Repository + "/releases/" + route + ManifestName);
            using (HttpClient client = CreateClient())
            {
                for (int redirect = 0; redirect <= 5; redirect++)
                {
                    if (!IsDownloadHost(uri)) throw new CryptographicException("更新清单重定向地址无效。");
                    using (HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            string json = await ReadSmallResponseAsync(response, ManifestLimit, token).ConfigureAwait(false);
                            SignedManifest manifest = VerifyManifest(json);
                            if (version != null && manifest.Version != version) throw new CryptographicException("更新清单版本不一致。");
                            CacheManifest(manifest.Version, json);
                            return json;
                        }
                        int status = (int)response.StatusCode;
                        if (redirect == 5 || (status != 301 && status != 302 && status != 303 && status != 307 && status != 308) || response.Headers.Location == null)
                            response.EnsureSuccessStatusCode();
                        Uri location = response.Headers.Location;
                        if (location == null) throw new InvalidDataException("更新清单重定向缺失。");
                        uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    }
                }
                throw new InvalidDataException("更新清单重定向过多。");
            }
        }

        private static string CachePath(string version)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZhehaoToolkit", "UpdateManifests", NormalizeVersion(version) + ".json");
        }

        public static void CacheManifest(string version, string signedJson)
        {
            SignedManifest manifest = VerifyManifest(signedJson);
            if (manifest.Version != NormalizeVersion(version)) throw new CryptographicException("缓存清单版本不一致。");
            lock (CacheGate) MemoryManifests[manifest.Version] = signedJson;
            string path = CachePath(manifest.Version);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temporary, signedJson, new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static async Task<SignedManifest> GetManifestAsync(string version, CancellationToken token)
        {
            version = NormalizeVersion(version);
            string json;
            lock (CacheGate) if (MemoryManifests.TryGetValue(version, out json)) return VerifyManifest(json);
            string path = CachePath(version);
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length <= ManifestLimit)
                {
                    json = File.ReadAllText(path, Encoding.UTF8);
                    SignedManifest cached = VerifyManifest(json);
                    if (cached.Version == version) return cached;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is CryptographicException || error is JsonException || error is FormatException) { }
            return VerifyManifest(await FetchManifestAsync(version, token).ConfigureAwait(false));
        }

        public static async Task<T> CheckAsync<T>(string repositoryUrl, string currentVersion, CancellationToken token)
        {
            if (repositoryUrl != "https://github.com/" + Repository) throw new InvalidDataException("更新仓库配置不一致。");
            try
            {
                string signed = await FetchManifestAsync(null, token).ConfigureAwait(false);
                SignedManifest manifest = VerifyManifest(signed);
                if (new Version(manifest.Version).CompareTo(new Version(NormalizeVersion(currentVersion))) <= 0) return default(T);
                string releaseUrl = "https://github.com/" + Repository + "/releases/tag/v" + manifest.Version;
                return (T)Activator.CreateInstance(typeof(T), new object[] { manifest.Version, manifest.Notes, new Uri(manifest.DownloadUrl), new Uri(releaseUrl), (long?)manifest.Size, manifest.Sha256 });
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { return default(T); }
            catch (CryptographicException error) { throw new InvalidDataException("更新信息校验失败。", error); }
            catch (KeyNotFoundException error) { throw new InvalidDataException("更新响应缺少必要字段。", error); }
        }

        public static async Task<T> VerifyAsync<T>(object unusedOriginalVerifier, string path, CancellationToken token)
        {
            using (SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return await VerifyAsync<T>(unusedOriginalVerifier, path, handle, token).ConfigureAwait(false);
        }

        public static async Task<T> VerifyAsync<T>(object unusedOriginalVerifier, string path, SafeFileHandle handle, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
            if (versionInfo.ProductName != Product || versionInfo.FileDescription != Product)
                throw new CryptographicException("更新包的产品名称无效。");
            string version = NormalizeVersion(versionInfo.ProductVersion);
            SignedManifest manifest = await GetManifestAsync(version, token).ConfigureAwait(false);
            if (RandomAccess.GetLength(handle) != manifest.Size) throw new CryptographicException("更新包长度与签名清单不一致。");
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[65536];
                long offset = 0;
                int read;
                while ((read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(), offset, token).ConfigureAwait(false)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                    offset += read;
                }
                string digest = Convert.ToHexString(hash.GetHashAndReset());
                if (offset != manifest.Size || !string.Equals(digest, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("更新包哈希与签名清单不一致。");
                return (T)Activator.CreateInstance(typeof(T), new object[] { path, digest, SigningKeyId, manifest.Version });
            }
        }
    }
}
