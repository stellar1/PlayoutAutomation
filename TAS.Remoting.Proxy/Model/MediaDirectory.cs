﻿using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using TAS.Remoting.Client;
using TAS.Common;
using TAS.Common.Interfaces;

namespace TAS.Remoting.Model
{
    public abstract class MediaDirectory : ProxyBase, IMediaDirectory
    {
        #pragma warning disable CS0649

        [JsonProperty(nameof(IMediaDirectory.DirectoryName))]
        private string _directoryName;

        [JsonProperty(nameof(IMediaDirectory.Folder))]
        private string _folder;

        [JsonProperty(nameof(IMediaDirectory.PathSeparator))]
        private char _pathSeparator;

        [JsonProperty(nameof(IMediaDirectory.IsInitialized))]
        private bool _isInitialized;

        [JsonProperty(nameof(IMediaDirectory.VolumeFreeSize))]
        private long _volumeFreeSize;

        [JsonProperty(nameof(IMediaDirectory.VolumeTotalSize))]
        private long _volumeTotalSize;

        #pragma warning restore

        public string DirectoryName { get { return _directoryName; } set { Set(value); } }

        public abstract IEnumerable<IMedia> GetFiles();
        
        public string Folder { get { return _folder; } set { Set(value); } }

        public char PathSeparator => _pathSeparator;

        public bool IsInitialized => _isInitialized;

        public long VolumeFreeSize => _volumeFreeSize;

        public long VolumeTotalSize => _volumeTotalSize;

        #region Event handling
        private event EventHandler<MediaEventArgs> MediaAddedEvent;
        public event EventHandler<MediaEventArgs> MediaAdded
        {
            add
            {
                EventAdd(MediaAddedEvent);
                MediaAddedEvent += value;
            }
            remove
            {
                MediaAddedEvent -= value;
                EventRemove(MediaAddedEvent);
            }
        }

        private event EventHandler<MediaEventArgs> MediaRemovedEvent;
        public event EventHandler<MediaEventArgs> MediaRemoved
        {
            add
            {
                EventAdd(MediaRemovedEvent);
                MediaRemovedEvent += value;
            }
            remove
            {
                MediaRemovedEvent -= value;
                EventRemove(MediaRemovedEvent);
            }
        }

        private event EventHandler<MediaEventArgs> MediaDeletedEvent;
        public event EventHandler<MediaEventArgs> MediaDeleted
        {
            add
            {
                EventAdd(MediaDeletedEvent);
                MediaDeletedEvent += value;
            }
            remove
            {
                MediaDeletedEvent -= value;
                EventRemove(MediaDeletedEvent);
            }
        }

        private event EventHandler<MediaEventArgs> MediaVerifiedEvent;
        public event EventHandler<MediaEventArgs> MediaVerified
        {
            add
            {
                EventAdd(MediaVerifiedEvent);
                MediaVerifiedEvent += value;
            }
            remove
            {
                MediaVerifiedEvent -= value;
                EventRemove(MediaVerifiedEvent);
            }
        }

        protected override void OnEventNotification(WebSocketMessage message)
        {
            if (message.MemberName == nameof(MediaAdded))
                    MediaAddedEvent?.Invoke(this, Deserialize<MediaEventArgs>(message));
            if (message.MemberName == nameof(MediaRemoved))
                MediaRemovedEvent?.Invoke(this, Deserialize<MediaEventArgs>(message));
            if (message.MemberName == nameof(MediaVerified))
                MediaVerifiedEvent?.Invoke(this, Deserialize<MediaEventArgs>(message));
        }

        #endregion // Ehent handling

        public bool DeleteMedia(IMedia media)
        {
            return Query<bool>(parameters: media );
        }

        public bool FileExists(string filename, string subfolder = null)
        {
            return Query<bool>(parameters: new object[] { filename, subfolder });
        }

        public bool DirectoryExists()
        {
            return Query<bool>();
        }

        public void Refresh()
        {
            Invoke();
        }

        public void SweepStaleMedia()
        {
            throw new NotImplementedException();
        }

        public override string ToString()
        {
            return DirectoryName;
        }

        public string GetUniqueFileName(string fileName)
        {
            return Query<string>(parameters: new object[] {fileName});
        }

        public abstract IMedia CreateMedia(IMediaProperties mediaProperties);
    }
}
