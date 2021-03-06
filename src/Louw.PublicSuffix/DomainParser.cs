﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Louw.PublicSuffix
{
    public class DomainParser
    {
        private readonly object _lockObject = new object();
        private DomainDataStructure _domainDataStructure = null;

        private readonly ITldRuleProvider _ruleProvider;
        private readonly AsyncLazy<bool> _rulesLoaded;

        public DomainParser(IEnumerable<TldRule> rules)
        {
            if (rules == null)
                throw new ArgumentNullException("rules");
            
            this.AddRules(rules);
            _rulesLoaded = new AsyncLazy<bool>(() => true);
        }

        public DomainParser(ITldRuleProvider ruleProvider)
        {
            if (ruleProvider == null)
                throw new ArgumentNullException("ruleProvider");

            _ruleProvider = ruleProvider;

            //Using AsyncLazy is thread safe way to initialize rules
            _rulesLoaded = new AsyncLazy<bool>(async delegate 
            {
                var rules = await _ruleProvider.BuildAsync().ConfigureAwait(false);
                AddRules(rules);
                return true;
            });
        }

        public async Task<DomainInfo> ParseAsync(string domain)
        {
            if (string.IsNullOrEmpty(domain))
            {
                return null;
            }

            var isRulesLoaded = await _rulesLoaded.Value.ConfigureAwait(false);
            if (!isRulesLoaded)
                throw new InvalidOperationException("Rules not loaded yet");

            //We use Uri methods to normalize host (So Punycode is converted to UTF-8
            if (!domain.Contains("://")) domain = string.Concat("https://", domain);
            Uri uri;
            if (!Uri.TryCreate(domain, UriKind.RelativeOrAbsolute, out uri))
            {
                return null;
            }
            string normalizedDomain = uri.Host;
            string normalizedHost = uri.GetComponents(UriComponents.NormalizedHost, UriFormat.UriEscaped); //Normalize Punycode

            var parts = normalizedHost
                .Split('.')
                .Reverse()
                .ToList();

            if (parts.Count == 0 || parts.Any(x => x.Equals("")))
            {
                return null;
            }

            var structure = this._domainDataStructure;
            var matches = new List<TldRule>();
            FindMatches(parts, structure, matches);

            //Sort so exceptions are first, then by biggest label count (with wildcards at bottom) 
            var sortedMatches = matches.OrderByDescending(x => x.Type == TldRuleType.WildcardException ? 1 : 0)
                .ThenByDescending(x => x.LabelCount)
                .ThenByDescending(x => x.Name);

            var winningRule = sortedMatches.FirstOrDefault();

            if (winningRule == null)
            {
                winningRule = new TldRule("*");
            }

            //Domain is TLD
            if (parts.Count == winningRule.LabelCount)
            {
                return null;
            }

            var domainName = new DomainInfo(normalizedDomain, winningRule);
            return domainName;
        }

        [Obsolete("Use ParseAsync instead")]
        public DomainInfo Get(string domain)
        {
            return ParseAsync(domain).Result;
        }

        private void FindMatches(IEnumerable<string> parts, DomainDataStructure structure, List<TldRule> matches)
        {
            if (structure.TldRule != null)
            {
                matches.Add(structure.TldRule);
            }

            var part = parts.FirstOrDefault();
            if (string.IsNullOrEmpty(part))
            {
                return;
            }

            DomainDataStructure foundStructure;
            if (structure.Nested.TryGetValue(part, out foundStructure))
            {
                FindMatches(parts.Skip(1), foundStructure, matches);
            }

            if (structure.Nested.TryGetValue("*", out foundStructure))
            {
                FindMatches(parts.Skip(1), foundStructure, matches);
            }
        }

        private void AddRules(IEnumerable<TldRule> tldRules)
        {
            System.Diagnostics.Debug.Assert(_domainDataStructure == null); //We can only load rules once
            _domainDataStructure = new DomainDataStructure("*", new TldRule("*"));

            foreach (var tldRule in tldRules)
            {
                this.AddRule(tldRule);
            }
        }

        private void AddRule(TldRule tldRule)
        {
            var structure = this._domainDataStructure;
            var domainPart = string.Empty;

            var parts = tldRule.Name.Split('.').Reverse().ToList();
            for (var i = 0; i < parts.Count; i++)
            {
                domainPart = parts[i];

                if (parts.Count - 1 > i)
                {
                    //Check if domain exists
                    if (!structure.Nested.ContainsKey(domainPart))
                    {
                        structure.Nested.Add(domainPart, new DomainDataStructure(domainPart));
                    }

                    structure = structure.Nested[domainPart];
                    continue;
                }

                //Check if domain exists
                if (structure.Nested.ContainsKey(domainPart))
                {
                    structure.Nested[domainPart].TldRule = tldRule;
                    continue;
                }

                structure.Nested.Add(domainPart, new DomainDataStructure(domainPart, tldRule));
            }
        }
    }
}
