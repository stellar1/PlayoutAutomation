﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using TAS.Remoting.Server;
using TAS.Common.Interfaces;
using TAS.Server.Media;
using TAS.Common;

namespace TAS.Server
{
    public class FileManager: DtoBase, IFileManager
    {
#pragma warning disable CS0169
        [JsonProperty]
        private readonly string Dummy; // at  least one property should be serialized to resolve references
#pragma warning restore
        private static readonly NLog.Logger Logger = NLog.LogManager.GetLogger(nameof(FileManager));
        private readonly SynchronizedCollection<IFileOperation> _queueSimpleOperation = new SynchronizedCollection<IFileOperation>();
        private readonly SynchronizedCollection<IFileOperation> _queueConvertOperation = new SynchronizedCollection<IFileOperation>();
        private readonly SynchronizedCollection<IFileOperation> _queueExportOperation = new SynchronizedCollection<IFileOperation>();
        private bool _isRunningSimpleOperation;
        private bool _isRunningConvertOperation;
        private bool _isRunningExportOperation;
        internal readonly TempDirectory TempDirectory;
        internal double ReferenceLoudness;

        internal FileManager(TempDirectory tempDirectory)
        {
            TempDirectory = tempDirectory;
        }
        
        public event EventHandler<FileOperationEventArgs> OperationAdded;
        public event EventHandler<FileOperationEventArgs> OperationCompleted;

        public IIngestOperation CreateIngestOperation(IIngestMedia sourceMedia, IMediaDirectory destDirectory)
        {
            var sourceDirectory = sourceMedia.Directory as IIngestDirectory;
            if (sourceDirectory == null)
                return null;
            return new IngestOperation(this)
            {
                Source = sourceMedia,
                DestDirectory = destDirectory,
                AudioVolume = sourceDirectory.AudioVolume,
                SourceFieldOrderEnforceConversion = sourceDirectory.SourceFieldOrder,
                AspectConversion = sourceDirectory.AspectConversion,
                LoudnessCheck = sourceDirectory.MediaLoudnessCheckAfterIngest,
                StartTC = sourceMedia.TcStart,
                Duration = sourceMedia.Duration
            };
        }
        public ILoudnessOperation CreateLoudnessOperation()
        {
            return new LoudnessOperation(this);
        }
        public IFileOperation CreateSimpleOperation() { return new FileOperation(this); }
        
        public IEnumerable<IFileOperation> GetOperationQueue()
        {
            List<IFileOperation> retList;
            lock (_queueSimpleOperation.SyncRoot)
                retList = new List<IFileOperation>(_queueSimpleOperation);
            lock (_queueConvertOperation.SyncRoot)
                retList.AddRange(_queueConvertOperation);
            lock (_queueExportOperation.SyncRoot)
                retList.AddRange(_queueExportOperation);
            return retList;
        }

        public void QueueList(IEnumerable<IFileOperation> operationList, bool toTop = false)
        {
            foreach (var operation in operationList)
                Queue(operation, toTop);
        }

        public void Queue(IFileOperation operation, bool toTop = false)
        {
            FileOperation op = operation as FileOperation;
            if (op != null)
                _queue(op, toTop);
        }

        public void CancelPending()
        {
            lock (_queueSimpleOperation.SyncRoot)
                _queueSimpleOperation.Where(op => op.OperationStatus == FileOperationStatus.Waiting).ToList().ForEach(op => op.Abort());
            lock (_queueConvertOperation.SyncRoot)
                _queueConvertOperation.Where(op => op.OperationStatus == FileOperationStatus.Waiting).ToList().ForEach(op => op.Abort());
            lock (_queueExportOperation.SyncRoot)
                _queueExportOperation.Where(op => op.OperationStatus == FileOperationStatus.Waiting).ToList().ForEach(op => op.Abort());
            Logger.Trace("Cancelled pending operations");
        }

        private void _queue(FileOperation operation, bool toTop)
        {
            operation.ScheduledTime = DateTime.UtcNow;
            operation.OperationStatus = FileOperationStatus.Waiting;
            Logger.Info("Operation scheduled: {0}", operation);
            NotifyOperation(OperationAdded, operation);

            if ((operation.Kind == TFileOperationKind.Copy || operation.Kind == TFileOperationKind.Move || operation.Kind == TFileOperationKind.Ingest))
            {
                IMedia destMedia = operation.Dest;
                if (destMedia != null)
                    destMedia.MediaStatus = TMediaStatus.CopyPending;
            }
            if (operation.Kind == TFileOperationKind.Ingest)
            {
                lock (_queueConvertOperation.SyncRoot)
                {
                    if (toTop)
                        _queueConvertOperation.Insert(0, operation);
                    else
                        _queueConvertOperation.Add(operation);
                    if (!_isRunningConvertOperation)
                    {
                        _isRunningConvertOperation = true;
                        ThreadPool.QueueUserWorkItem(o => _runOperation(_queueConvertOperation, ref _isRunningConvertOperation));
                    }
                }
            }
            if (operation.Kind == TFileOperationKind.Export)
            {
                lock (_queueExportOperation.SyncRoot)
                {
                    if (toTop)
                        _queueExportOperation.Insert(0, operation);
                    else
                        _queueExportOperation.Add(operation);
                    if (!_isRunningExportOperation)
                    {
                        _isRunningExportOperation = true;
                        ThreadPool.QueueUserWorkItem(o => _runOperation(_queueExportOperation, ref _isRunningExportOperation));
                    }
                }
            }
            if (operation.Kind == TFileOperationKind.Copy
                || operation.Kind == TFileOperationKind.Delete
                || operation.Kind == TFileOperationKind.Loudness
                || operation.Kind == TFileOperationKind.Move)
            {
                lock (_queueSimpleOperation.SyncRoot)
                {
                    if (toTop)
                        _queueSimpleOperation.Insert(0, operation);
                    else
                        _queueSimpleOperation.Add(operation);
                    if (!_isRunningSimpleOperation)
                    {
                        _isRunningSimpleOperation = true;
                        ThreadPool.QueueUserWorkItem(o => _runOperation(_queueSimpleOperation, ref _isRunningSimpleOperation));
                    }
                }
            }
        }

        private void _runOperation(SynchronizedCollection<IFileOperation> queue, ref bool queueRunningIndicator)
        {
            FileOperation op;
            lock (queue.SyncRoot)
                op = queue.FirstOrDefault() as FileOperation;
            while (op != null)
            {
                try
                {
                    queue.Remove(op);
                    if (!op.IsAborted)
                    {
                        if (op.Execute())
                        {
                            NotifyOperation(OperationCompleted, op);
                            op.Dispose();
                        }
                        else
                        {
                            if (op.TryCount > 0)
                            {
                                System.Threading.Thread.Sleep(500);
                                queue.Add(op);
                            }
                            else
                            {
                                op.Fail();
                                NotifyOperation(OperationCompleted, op);
                                if (op.Dest?.FileExists() == true)
                                    op.Dest.Delete();
                                op.Dispose();
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, "RunOperation exception");
                }
                lock (queue.SyncRoot)
                    op = queue.FirstOrDefault() as FileOperation;
            }
            lock (queue.SyncRoot)
                queueRunningIndicator = false;
        }

        private void NotifyOperation(EventHandler<FileOperationEventArgs> handler, IFileOperation operation)
        {
            handler?.Invoke(this, new FileOperationEventArgs(operation));
        }
    }


}
