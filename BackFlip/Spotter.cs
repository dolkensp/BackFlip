using Azi.Amazon.CloudDrive;
using Azi.Amazon.CloudDrive.JsonObjects;
using Dolkens.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BackFlip
{
    public class Spotter : ITokenUpdateListener, IDisposable
    {
        private String _clientId;
        private String _clientSecret;
        private Boolean _loggedIn;
        private AmazonDrive _drive;
        private MD5 _md5 = MD5.Create();

        public Spotter(String clientId, String clientSecret)
        {
            this._clientId = clientId;
            this._clientSecret = clientSecret;

            if (!this.Login().Result)
            {
                throw new InvalidOperationException("Unable to initialize connection");
            }
        }
        
        private async Task<Boolean> Login()
        {
            this._drive = this._drive ?? new AmazonDrive(this._clientId, this._clientSecret) { OnTokenUpdate = this };

            if (!this._loggedIn)
            {
                if (File.Exists("login.json"))
                {
                    var tokens = File.ReadAllText("login.json").FromJSON<SimpleToken>();
                    if (await this._drive.AuthenticationByTokens(String.Empty, tokens.refresh_token, DateTime.Now))
                    {
                        this._loggedIn = true;
                    }
                }
            }

            if (!this._loggedIn)
            {
                this._loggedIn = await this._drive.AuthenticationByExternalBrowser(CloudDriveScopes.ReadAll | CloudDriveScopes.Write, TimeSpan.FromMinutes(5));
            }

            return this._loggedIn;
        }
        
        private class FileMeta
        {
            public DateTime CreationTimeUtc { get; set; }
            public DateTime LastWriteTimeUtc { get; set; }
            public String LatestHash { get; set; }
            public String FileName { get; set; }
        }

        private static readonly Int32 MAX_BANDWIDTH = 0;
        private static readonly TimeSpan NEAREST_SECOND = TimeSpan.FromSeconds(1);

        public async Task<AmazonNode> GetAmazonNode(String path, AmazonNode parentNode = null)
        {
            var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

            var lastNode = parentNode ?? await this._drive.Nodes.GetRoot();

            foreach (var part in parts)
            {
                var newNode = await this._drive.Nodes.GetChild(lastNode.id, part);

                if (newNode == null || newNode.kind != AmazonNodeKind.FOLDER)
                {
                    newNode = await this._drive.Nodes.CreateFolder(lastNode.id, $"{part}");
                }

                if (newNode == null)
                {
                    return null;
                }

                lastNode = newNode;
            }

            return lastNode;
        }

        public async Task<Boolean> Sync(String localRoot, String remoteRoot)
        {
            return await this.Sync(
                localRoot: localRoot,
                remoteRoot: remoteRoot,
                relativePath: String.Empty,
                whitelist: new List<Regex> { },
                blacklist: new List<Regex> { new Regex(@"\.backflip$", RegexOptions.IgnoreCase) });
        }

        private async Task<Boolean> Sync(String localRoot, String remoteRoot, String relativePath, List<Regex> whitelist, List<Regex> blacklist)
        {
            var basePath = Path.Combine(localRoot, relativePath);

            var localWhitelist = new List<Regex>(whitelist);
            var localBlacklist = new List<Regex>(blacklist);

            if (File.Exists(Path.Combine(basePath, ".flipignore")))
            {
                foreach (var line in File.ReadAllLines(Path.Combine(basePath, ".flipignore")))
                {
                    var rule = line.Trim();

                    if (!String.IsNullOrWhiteSpace(rule) && !rule.StartsWith("#"))
                    {
                        rule = rule.Replace(@"/", @"\\");
                        rule = rule.Replace(@".", @"\.");
                        rule = rule.Replace(@"*", @".*");
                        rule = rule.Replace(@"$", @"\$");

                        rule = rule.Replace(@".*$", "");
                        rule = rule.TrimEnd('\\');

                        rule = $"{rule}$";

                        if (rule.StartsWith("!"))
                        {
                            localWhitelist.Add(new Regex(rule, RegexOptions.IgnoreCase));
                        }
                        else
                        {
                            localBlacklist.Add(new Regex(rule, RegexOptions.IgnoreCase));
                        }
                    }
                }
            }

            var syncResult = true;
            var fileTableJson = String.Empty;
            var destinationNode = await this.GetAmazonNode(Path.Combine(remoteRoot, relativePath));
            var manifestRoot = await this.GetAmazonNode(".backflip", destinationNode);
            var manifest = await this._drive.Nodes.GetChild(manifestRoot.id, ".backflip");

            if (manifest != null)
            {
                MemoryStream memoryStream = new MemoryStream();

                await this._drive.Files.Download(manifest.id, memoryStream);
                
                memoryStream.Seek(0, SeekOrigin.Begin);

                fileTableJson = Encoding.UTF8.GetString(memoryStream.ReadAllBytes());
            }

            var fileTable = fileTableJson.FromJSON<Dictionary<String, FileMeta>>() ?? new Dictionary<String, FileMeta> { };

            // TODO: Cleanup Remote Temp Files
            
            foreach (var filePath in Directory.GetFiles(basePath))
            {
                if (!localWhitelist.Any(f => f.IsMatch(filePath)) &&
                     localBlacklist.Any(f => f.IsMatch(filePath))) continue;

                var fileName = Path.GetFileName(filePath);

                if (!fileTable.ContainsKey(fileName))
                {
                    fileTable[fileName] = new FileMeta { };
                }

                fileTable[fileName].FileName = fileName;

                var creationTimeUtc = File.GetCreationTimeUtc(filePath).TrimTo(NEAREST_SECOND);
                var lastWriteTimeUtc = File.GetLastWriteTimeUtc(filePath).TrimTo(NEAREST_SECOND);

                if (Utils.Max(fileTable[fileName].LastWriteTimeUtc, fileTable[fileName].CreationTimeUtc) < 
                    Utils.Max(lastWriteTimeUtc, creationTimeUtc))
                {
                    using (var fileStream = File.OpenRead(filePath))
                    {
                        var fileHash = this._md5.ComputeHash(fileStream).ToHex();

                        if (fileTable[fileName].LatestHash != fileHash)
                        {
                            #region Upload New Node

                            try
                            {
                                #region Detect Existing Nodes

                                var newNode = await this._drive.Nodes.GetChild(destinationNode.id, fileName);
                                var oldNode = newNode;

                                if (newNode != null)
                                {
                                    newNode = await this._drive.Nodes.GetNodeByMD5(fileHash);
                                }

                                #endregion

                                if (newNode == null ||
                                    newNode.id != oldNode.id ||
                                    newNode.name != fileName)
                                {
                                    Console.WriteLine($"Uploading {Path.Combine(relativePath, fileName)}");

                                    newNode = await this._drive.Files.UploadNew(destinationNode.id, $".{DateTime.Now:yyyyMMddHHmm}-{fileHash}.tmp", () =>
                                {
                                    var stream = new ThrottledStream(File.OpenRead(filePath), MAX_BANDWIDTH);

                                    return stream;
                                }, true);

                                    #region Archive Old Node If It Exists

                                    if (oldNode != null)
                                    {
                                        oldNode = await this._drive.Nodes.Rename(oldNode.id, $"{oldNode.createdDate:yyyyMMddHHmmss}.{oldNode.name}");
                                        oldNode = await this._drive.Nodes.Move(oldNode.id, destinationNode.id, manifestRoot.id);
                                    }

                                    #endregion

                                    if (newNode != null)
                                    {
                                        newNode = await this._drive.Nodes.Rename(newNode.id, $"{fileName}");
                                    }
                                }

                                if (newNode != null)
                                {
                                    fileTable[fileName].CreationTimeUtc = creationTimeUtc;
                                    fileTable[fileName].LastWriteTimeUtc = lastWriteTimeUtc;
                                    fileTable[fileName].LatestHash = fileHash;
                                }
                            }
                            catch (IOException)
                            {
                                Console.WriteLine($"Skipping {Path.Combine(relativePath, fileName)} - unable to access file");
                            }

                            #endregion
                        }
                        else
                        {
                            fileTable[fileName].CreationTimeUtc = creationTimeUtc;
                            fileTable[fileName].LastWriteTimeUtc = lastWriteTimeUtc;
                        }
                    }
                }
            }

            Func<Stream> streamBuilder = () =>
            {
                var stream = new ThrottledStream(new MemoryStream(Encoding.UTF8.GetBytes(fileTable.ToJSON())), MAX_BANDWIDTH);

                return stream;
            };

            if (manifest == null)
            {
                await this._drive.Files.UploadNew(manifestRoot.id, $".backflip", streamBuilder, true);
            }
            else
            {
                await this._drive.Files.Overwrite(manifest.id, streamBuilder);
            }

            foreach (var dirPath in Directory.GetDirectories(basePath))
            {
                if (!localWhitelist.Any(f => f.IsMatch(dirPath)) &&
                     localBlacklist.Any(f => f.IsMatch(dirPath))) continue;

                syncResult &= await this.Sync(localRoot, remoteRoot, Path.Combine(relativePath, Path.GetFileName(dirPath)), localWhitelist, localBlacklist);
            }

            return syncResult;
        }

        public void OnTokenUpdated(String access_token, String refresh_token, DateTime expires_in)
        {
            File.WriteAllText("login.json", new SimpleToken
            {
                refresh_token = refresh_token,
            }.ToJSON());
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}
