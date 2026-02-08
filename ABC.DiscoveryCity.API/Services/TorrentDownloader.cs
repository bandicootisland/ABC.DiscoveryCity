using MonoTorrent;
using MonoTorrent.Client;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Net;

namespace ABC.DiscoveryCity.API.Services
{
    public class TorrentDownloader
    {
        private static ClientEngine _engine;
        private static readonly object _lock = new object();

        public TorrentDownloader()
        {
            InitializeEngine();
        }

        private void InitializeEngine()
        {
            if (_engine != null) return;

            lock (_lock)
            {
                if (_engine != null) return;

                // Configure the engine with default settings
                var settings = new EngineSettingsBuilder()
                    .ToSettings();

                _engine = new ClientEngine(settings);
            }
        }

        public async Task<Torrent> LoadAsync(string torrentFilePath)
        {
            var torrent = await Torrent.LoadAsync(torrentFilePath);
            return torrent;
        }

        public async Task<TorrentManager> ManageAsync(Torrent torrent, string saveDirectory)
        {
            // Check if this torrent is already being managed to avoid duplicates
            foreach (var existingManager in _engine.Torrents)
            {
                if (existingManager.Torrent != null && existingManager.InfoHashes == torrent.InfoHashes)
                {
                    return existingManager;
                }
            }

            var manager = await _engine.AddAsync(torrent, saveDirectory);
            return manager;
        }

        public async Task StopAllAsync()
        {
            await _engine.StopAllAsync();
        }
    }
}
