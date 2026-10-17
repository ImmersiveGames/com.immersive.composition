using System.Collections.Generic;
using System.Linq;

namespace Immersive.Composition
{
    internal sealed class CompositionGraphValidation
    {
        public readonly HashSet<ContentKey> InvalidKeys = new HashSet<ContentKey>();
        public readonly List<CompositionIssue> Issues = new List<CompositionIssue>();
    }

    internal static class CompositionGraph
    {
        public static CompositionGraphValidation Validate(
            IReadOnlyDictionary<ContentKey, PartDefinition> definitions,
            IEnumerable<CompositionIssue> winnerIssues)
        {
            var validation = new CompositionGraphValidation();
            if (winnerIssues != null)
            {
                foreach (CompositionIssue issue in winnerIssues)
                {
                    validation.Issues.Add(issue);
                    if (issue.Key.IsValid)
                    {
                        validation.InvalidKeys.Add(issue.Key);
                    }
                }
            }

            foreach (KeyValuePair<ContentKey, PartDefinition> pair in definitions.OrderBy(item => item.Key))
            {
                for (int i = 0; i < pair.Value.Dependencies.Count; i++)
                {
                    ContentKey dependency = pair.Value.Dependencies[i];
                    if (!definitions.ContainsKey(dependency))
                    {
                        validation.InvalidKeys.Add(pair.Key);
                        validation.Issues.Add(new CompositionIssue(
                            CompositionIssueCode.MissingDependency,
                            pair.Key,
                            "Required content key '" + dependency.Value + "' has no winning contribution."));
                    }
                }
            }

            var colors = new Dictionary<ContentKey, byte>();
            var path = new List<ContentKey>();
            foreach (ContentKey key in definitions.Keys.OrderBy(item => item))
            {
                Visit(key, definitions, colors, path, validation);
            }

            bool changed;
            do
            {
                changed = false;
                foreach (KeyValuePair<ContentKey, PartDefinition> pair in definitions.OrderBy(item => item.Key))
                {
                    if (validation.InvalidKeys.Contains(pair.Key))
                    {
                        continue;
                    }

                    for (int i = 0; i < pair.Value.Dependencies.Count; i++)
                    {
                        if (validation.InvalidKeys.Contains(pair.Value.Dependencies[i]))
                        {
                            validation.InvalidKeys.Add(pair.Key);
                            changed = true;
                            validation.Issues.Add(new CompositionIssue(
                                CompositionIssueCode.MissingDependency,
                                pair.Key,
                                "A dependency is invalid, so this part cannot be committed."));
                            break;
                        }
                    }
                }
            }
            while (changed);

            return validation;
        }

        public static HashSet<ContentKey> ExpandDependents(
            IEnumerable<ContentKey> seeds,
            params IReadOnlyDictionary<ContentKey, PartDefinition>[] graphs)
        {
            var affected = new HashSet<ContentKey>(seeds);
            var queue = new Queue<ContentKey>(affected);
            while (queue.Count > 0)
            {
                ContentKey dependency = queue.Dequeue();
                for (int graphIndex = 0; graphIndex < graphs.Length; graphIndex++)
                {
                    foreach (KeyValuePair<ContentKey, PartDefinition> pair in graphs[graphIndex])
                    {
                        if (Contains(pair.Value.Dependencies, dependency) && affected.Add(pair.Key))
                        {
                            queue.Enqueue(pair.Key);
                        }
                    }
                }
            }

            return affected;
        }

        public static HashSet<ContentKey> ExpandDependencyClosure(
            IEnumerable<ContentKey> seeds,
            params IReadOnlyDictionary<ContentKey, PartDefinition>[] graphs)
        {
            var closure = new HashSet<ContentKey>(seeds);
            var queue = new Queue<ContentKey>(closure);
            while (queue.Count > 0)
            {
                ContentKey key = queue.Dequeue();
                for (int graphIndex = 0; graphIndex < graphs.Length; graphIndex++)
                {
                    if (!graphs[graphIndex].TryGetValue(key, out PartDefinition definition))
                    {
                        continue;
                    }

                    for (int i = 0; i < definition.Dependencies.Count; i++)
                    {
                        if (closure.Add(definition.Dependencies[i]))
                        {
                            queue.Enqueue(definition.Dependencies[i]);
                        }
                    }
                }
            }

            return closure;
        }

        public static List<ContentKey> TopologicalOrder(
            IEnumerable<ContentKey> keys,
            IReadOnlyDictionary<ContentKey, PartDefinition> definitions)
        {
            var selected = new HashSet<ContentKey>(keys);
            var visited = new HashSet<ContentKey>();
            var visiting = new HashSet<ContentKey>();
            var result = new List<ContentKey>();
            foreach (ContentKey key in selected.OrderBy(item => item))
            {
                VisitForOrder(key, selected, definitions, visited, visiting, result);
            }

            return result;
        }

        private static void Visit(
            ContentKey key,
            IReadOnlyDictionary<ContentKey, PartDefinition> definitions,
            IDictionary<ContentKey, byte> colors,
            IList<ContentKey> path,
            CompositionGraphValidation validation)
        {
            if (colors.TryGetValue(key, out byte color))
            {
                if (color == 2)
                {
                    return;
                }

                if (color == 1)
                {
                    int cycleStart = path.IndexOf(key);
                    for (int i = cycleStart; i < path.Count; i++)
                    {
                        if (validation.InvalidKeys.Add(path[i]))
                        {
                            validation.Issues.Add(new CompositionIssue(
                                CompositionIssueCode.DependencyCycle,
                                path[i],
                                "The content dependency graph contains a cycle."));
                        }
                    }

                    return;
                }
            }

            if (!definitions.TryGetValue(key, out PartDefinition definition))
            {
                return;
            }

            colors[key] = 1;
            path.Add(key);
            for (int i = 0; i < definition.Dependencies.Count; i++)
            {
                if (definitions.ContainsKey(definition.Dependencies[i]))
                {
                    Visit(definition.Dependencies[i], definitions, colors, path, validation);
                }
            }

            path.RemoveAt(path.Count - 1);
            colors[key] = 2;
        }

        private static void VisitForOrder(
            ContentKey key,
            HashSet<ContentKey> selected,
            IReadOnlyDictionary<ContentKey, PartDefinition> definitions,
            HashSet<ContentKey> visited,
            HashSet<ContentKey> visiting,
            List<ContentKey> result)
        {
            if (visited.Contains(key) || !visiting.Add(key))
            {
                return;
            }

            if (definitions.TryGetValue(key, out PartDefinition definition))
            {
                for (int i = 0; i < definition.Dependencies.Count; i++)
                {
                    ContentKey dependency = definition.Dependencies[i];
                    if (selected.Contains(dependency))
                    {
                        VisitForOrder(dependency, selected, definitions, visited, visiting, result);
                    }
                }
            }

            visiting.Remove(key);
            visited.Add(key);
            result.Add(key);
        }

        private static bool Contains(IReadOnlyList<ContentKey> keys, ContentKey value)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] == value)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
