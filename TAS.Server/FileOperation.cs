﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json;
using TAS.Remoting.Server;
using TAS.Common;
using TAS.Common.Interfaces;
using TAS.Server.Media;

namespace TAS.Server
{
    public class FileOperation : DtoBase, IFileOperation
    {
        [JsonProperty]
        public TFileOperationKind Kind { get; set; }

        private readonly object _destMediaLock = new object();

        private IMedia _sourceMedia;
        private static readonly NLog.Logger Logger = NLog.LogManager.GetLogger(nameof(FileOperation));
        private IMediaProperties _destMediaProperties;
        private int _tryCount = 15;
        private DateTime _scheduledTime;
        private DateTime _startTime;
        private DateTime _finishedTime;
        private FileOperationStatus _operationStatus;
        private int _progress;
        private bool _isIndeterminate;
        private readonly SynchronizedCollection<string> _operationOutput = new SynchronizedCollection<string>();
        private readonly SynchronizedCollection<string> _operationWarning = new SynchronizedCollection<string>();

        protected readonly FileManager OwnerFileManager;
        protected bool Aborted;

        internal FileOperation(FileManager ownerFileManager)
        {
            OwnerFileManager = ownerFileManager;
        }

#if DEBUG
        ~FileOperation()
        {
            Debug.WriteLine("{0} finalized: {1}", GetType(), this);
        }
#endif
        
        [JsonProperty]
        public IMediaProperties DestProperties { get { return _destMediaProperties; } set { SetField(ref _destMediaProperties, value, nameof(Title)); } }

        [JsonProperty]
        public IMediaDirectory DestDirectory { get; set; }

        [JsonProperty]
        public IMedia Source { get { return _sourceMedia; } set { SetField(ref _sourceMedia, value); } }

        internal MediaBase Dest { get; set; }

        [JsonProperty]
        public int TryCount
        {
            get { return _tryCount; }
            set { SetField(ref _tryCount, value); }
        }
        
        [JsonProperty]
        public int Progress
        {
            get { return _progress; }
            set
            {
                if (value > 0 && value <= 100)
                    SetField(ref _progress, value);
                IsIndeterminate = false;
            }
        }

        [JsonProperty]
        public DateTime ScheduledTime
        {
            get { return _scheduledTime; }
            internal set
            {
                if (SetField(ref _scheduledTime, value))
                    AddOutputMessage("Operation scheduled");
            }
        }

        [JsonProperty]
        public DateTime StartTime
        {
            get { return _startTime; }
            protected set { SetField(ref _startTime, value); }
        }

        [JsonProperty]
        public DateTime FinishedTime 
        {
            get { return _finishedTime; }
            protected set { SetField(ref _finishedTime, value); }
        }

        [JsonProperty]
        public FileOperationStatus OperationStatus
        {
            get { return _operationStatus; }
            set
            {
                if (SetField(ref _operationStatus, value))
                {
                    TIngestStatus newIngestStatus;
                        switch (value)
                        {
                            case FileOperationStatus.Finished:
                                newIngestStatus = TIngestStatus.Ready;
                                break;
                            case FileOperationStatus.Waiting:
                            case FileOperationStatus.InProgress:
                                newIngestStatus = TIngestStatus.InProgress;
                                break;
                            default:
                                newIngestStatus = TIngestStatus.Unknown;
                                break;
                        }
                    var im = _sourceMedia as IngestMedia;
                    if (im != null)
                        im.IngestStatus = newIngestStatus;
                    var am = _sourceMedia as ArchiveMedia;
                    if (am != null)
                        am.IngestStatus = newIngestStatus;

                    EventHandler h;
                    if (value == FileOperationStatus.Finished)
                    {
                        Progress = 100;
                        FinishedTime = DateTime.UtcNow;
                        h = Success;
                        h?.Invoke(this, EventArgs.Empty);
                        h = Finished;
                        h?.Invoke(this, EventArgs.Empty);
                    }
                    if (value == FileOperationStatus.Failed)
                    {
                        Progress = 0;
                        h = Failure;
                        h?.Invoke(this, EventArgs.Empty);
                        h = Finished;
                        h?.Invoke(this, EventArgs.Empty);
                    }
                    if (value == FileOperationStatus.Aborted)
                    {
                        IsIndeterminate = false;
                        h = Failure;
                        h?.Invoke(this, EventArgs.Empty);
                        h = Finished;
                        h?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        [JsonProperty]
        public bool IsIndeterminate
        {
            get { return _isIndeterminate; }
            set { SetField(ref _isIndeterminate, value); }
        }


        [JsonProperty]
        public bool IsAborted
        {
            get { return Aborted; }
            private set
            {
                if (SetField(ref Aborted, value))
                {
                    lock (_destMediaLock)
                    {
                        if (Dest != null && Dest.FileExists())
                            Dest.Delete();
                    }
                    IsIndeterminate = false;
                    OperationStatus = FileOperationStatus.Aborted;
                }
            }
        }

        [JsonProperty]
        public virtual string Title => DestDirectory == null ?
            string.Format("{0} {1}", Kind, Source)
            :
            string.Format("{0} {1} -> {2}", Kind, Source, DestDirectory.DirectoryName);

        [JsonProperty]
        public List<string> OperationWarning { get { lock (_operationWarning.SyncRoot) return _operationWarning.ToList(); } }

        [JsonProperty]
        public List<string> OperationOutput { get { lock (_operationOutput.SyncRoot) return _operationOutput.ToList(); } }

        public virtual void Abort()
        {
            IsAborted = true;
        }

        public event EventHandler Success;
        public event EventHandler Failure;
        public event EventHandler Finished;


        // utility methods
        internal virtual bool Execute()
        {
            if (InternalExecute())
            {
                OperationStatus = FileOperationStatus.Finished;
            }
            else
                TryCount--;
            return OperationStatus == FileOperationStatus.Finished;
        }

        internal void Fail()
        {
            OperationStatus = FileOperationStatus.Failed;
            lock (_destMediaLock)
            {
                if (Dest != null && Dest.FileExists())
                    Dest.Delete();
            }
            Logger.Info($"Operation failed: {Title}");
        }

        protected void AddOutputMessage(string message)
        {
            _operationOutput.Add(string.Format("{0} {1}", DateTime.Now, message));
            NotifyPropertyChanged(nameof(OperationOutput));
            Logger.Info("{0}: {1}", Title, message);
        }

        protected void AddWarningMessage(string message)
        {
            _operationWarning.Add(message);
            NotifyPropertyChanged(nameof(OperationWarning));
        }

        protected virtual void CreateDestMediaIfNotExists()
        {
            lock (_destMediaLock)
            {
                if (Dest == null)
                    Dest = (MediaBase)DestDirectory.CreateMedia(DestProperties ?? Source);
            }
        }
        
        private bool InternalExecute()
        {
            AddOutputMessage($"Operation {Title} started");
            StartTime = DateTime.UtcNow;
            OperationStatus = FileOperationStatus.InProgress;
            if (!(Source is MediaBase source))
                return false;
            switch (Kind)
            {
                case TFileOperationKind.None:
                    return true;
                case TFileOperationKind.Ingest:
                case TFileOperationKind.Export:
                    throw new InvalidOperationException("Invalid operation kind");
                case TFileOperationKind.Copy:
                    if (!File.Exists(source.FullPath) || !Directory.Exists(DestDirectory.Folder))
                        return false;
                    try
                    {
                        lock (_destMediaLock)
                        {
                            CreateDestMediaIfNotExists();
                            if (!(Dest.FileExists()
                                  && File.GetLastWriteTimeUtc(source.FullPath).Equals(File.GetLastWriteTimeUtc(Dest.FullPath))
                                  && File.GetCreationTimeUtc(source.FullPath).Equals(File.GetCreationTimeUtc(Dest.FullPath))
                                  && Source.FileSize.Equals(Dest.FileSize)))
                            {
                                Dest.MediaStatus = TMediaStatus.Copying;
                                IsIndeterminate = true;
                                if (!source.CopyMediaTo(Dest, ref Aborted))
                                    return false;
                            }
                            Dest.MediaStatus = TMediaStatus.Copied;
                            ThreadPool.QueueUserWorkItem(o => Dest.Verify());
                            AddOutputMessage($"Copy operation {Title} finished");
                            return true;
                        }
                    }
                    catch (Exception e)
                    {
                        AddOutputMessage($"Copy operation {Title} failed with {e.Message}");
                    }
                    return false;
                case TFileOperationKind.Delete:
                    try
                    {
                        if (Source.Delete())
                        {
                            AddOutputMessage($"Delete operation {Title} finished"); 
                            return true;
                        }
                    }
                    catch (Exception e)
                    {
                        AddOutputMessage($"Delete operation {Title} failed with {e.Message}");
                    }
                    return false;
                case TFileOperationKind.Move:
                    if (!File.Exists(source.FullPath) || !Directory.Exists(DestDirectory.Folder))
                        return false;
                    try
                    {
                        CreateDestMediaIfNotExists();
                        if (Dest.FileExists())
                        {
                            if (File.GetLastWriteTimeUtc(source.FullPath).Equals(File.GetLastWriteTimeUtc(Dest.FullPath))
                                && File.GetCreationTimeUtc(source.FullPath).Equals(File.GetCreationTimeUtc(Dest.FullPath))
                                && source.FileSize.Equals(Dest.FileSize))
                            {
                                source.Delete();
                                return true;
                            }
                            else
                            if (!Dest.Delete())
                            {
                                AddOutputMessage("Move operation failed - destination media not deleted");
                                return false;
                            }
                        }
                        IsIndeterminate = true;
                        Dest.MediaStatus = TMediaStatus.Copying;
                        FileUtils.CreateDirectoryIfNotExists(Path.GetDirectoryName(Dest.FullPath));
                        File.Move(source.FullPath, Dest.FullPath);
                        File.SetCreationTimeUtc(Dest.FullPath, File.GetCreationTimeUtc(source.FullPath));
                        File.SetLastWriteTimeUtc(Dest.FullPath, File.GetLastWriteTimeUtc(source.FullPath));
                        Dest.MediaStatus = TMediaStatus.Copied;
                        ThreadPool.QueueUserWorkItem(o => Dest.Verify());
                        AddOutputMessage("Move operation finished");
                        Debug.WriteLine(this, "File operation succeed");
                        return true;
                    }
                    catch (Exception e)
                    {
                        AddOutputMessage($"Move operation {Title} failed with {e.Message}");
                    }
                    return false;
                default:
                    return false;
            }
        }
        
    }
}
