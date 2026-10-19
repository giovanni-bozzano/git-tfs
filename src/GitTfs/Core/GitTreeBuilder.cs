using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;

namespace GitTfs.Core
{
    public class GitTreeBuilder : IGitTreeBuilder
    {
        private readonly TreeDefinition _treeDefinition;
        private readonly ObjectDatabase _objectDatabase;
        private readonly Dictionary<string, KeyValuePair<string, LibGit2Sharp.Mode>> _pendingFiles = new Dictionary<string, KeyValuePair<string, LibGit2Sharp.Mode>>(StringComparer.Ordinal);

        public GitTreeBuilder(ObjectDatabase objectDatabase)
        {
            _treeDefinition = new TreeDefinition();
            _objectDatabase = objectDatabase;
        }

        public GitTreeBuilder(ObjectDatabase objectDatabase, Tree tree)
        {
            _treeDefinition = TreeDefinition.From(tree);
            _objectDatabase = objectDatabase;
        }

        public void Add(string path, string file, LibGit2Sharp.Mode mode)
        {
            _pendingFiles[path] = new KeyValuePair<string, LibGit2Sharp.Mode>(file, mode);
            _treeDefinition.Add(path, file, mode);
        }

        public void Remove(string path)
        {
            _pendingFiles.Remove(path);
            var prefix = path + "/";
            foreach (var key in _pendingFiles.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _pendingFiles.Remove(key);
            _treeDefinition.Remove(path);
        }

        public Tree GetTree()
        {
            if (_pendingFiles.Count > 1)
            {
                var pending = _pendingFiles.ToArray();
                var blobs = new Blob[pending.Length];
                Parallel.For(0, pending.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    i => blobs[i] = _objectDatabase.CreateBlob(pending[i].Value.Key));
                for (var i = 0; i < pending.Length; i++)
                    _treeDefinition.Add(pending[i].Key, blobs[i], pending[i].Value.Value);
            }
            _pendingFiles.Clear();
            return _objectDatabase.CreateTree(_treeDefinition);
        }
    }
}
