// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Tools.Internal;

namespace Microsoft.DotNet.Watcher.Internal
{
    public class FileSetWatcher : IDisposable
    {
        private readonly FileWatcher _fileWatcher;
        private readonly IReadOnlySet<string> _fileSet;

        public FileSetWatcher(IReadOnlySet<string> fileSet, IReporter reporter, bool usePollingWatcher)
        {
            Ensure.NotNull(fileSet, nameof(fileSet));

            _fileSet = fileSet;
            _fileWatcher = new FileWatcher(reporter, usePollingWatcher);
        }

        public async Task<string> GetChangedFileAsync(CancellationToken cancellationToken, Action startedWatching)
        {
            foreach (var file in _fileSet)
            {
                _fileWatcher.WatchDirectory(Path.GetDirectoryName(file));
            }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetResult(null));

            void FileChangedCallback(string path, bool newFile)
            {
                if (_fileSet.Contains(path))
                {
                    tcs.TrySetResult(path);
                }
            }

            _fileWatcher.OnFileChange += FileChangedCallback;
            startedWatching();
            var changedFile = await tcs.Task;
            _fileWatcher.OnFileChange -= FileChangedCallback;

            return changedFile;
        }

        public Task<string> GetChangedFileAsync(CancellationToken cancellationToken)
        {
            return GetChangedFileAsync(cancellationToken, () => { });
        }

        public void Dispose()
        {
            _fileWatcher.Dispose();
        }
    }
}