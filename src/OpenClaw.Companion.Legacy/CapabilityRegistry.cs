using System;
using System.Collections.Generic;

namespace OpenClaw
{
    /// <summary>
    /// A registry class that maps capability names (strings) to handler delegates.
    /// Enables extensible method dispatch for the MCP HTTP server.
    /// .NET Framework 2.0 compatible — no LINQ, no var, no lambda (use delegate keyword).
    /// </summary>
    public sealed class CapabilityRegistry
    {
        /// <summary>
        /// Delegate for handling a capability method.
        /// </summary>
        /// <param name="paramsJson">The JSON-RPC params as a raw JSON string.</param>
        /// <returns>Result JSON string to embed in the JSON-RPC response.</returns>
        public delegate string CapabilityHandler(string paramsJson);

        private Dictionary<string, CapabilityHandler> _handlers;

        /// <summary>
        /// Creates an empty capability registry.
        /// </summary>
        public CapabilityRegistry()
        {
            _handlers = new Dictionary<string, CapabilityHandler>();
        }

        /// <summary>
        /// Register a handler for a named capability.
        /// </summary>
        /// <param name="name">The JSON-RPC method name (e.g. "system.notify").</param>
        /// <param name="handler">Delegate that takes params JSON and returns result JSON.</param>
        public void Register(string name, CapabilityHandler handler)
        {
            if (name == null)
            {
                throw new ArgumentNullException("name");
            }

            if (handler == null)
            {
                throw new ArgumentNullException("handler");
            }

            _handlers[name] = handler;
        }

        /// <summary>
        /// Try to retrieve a handler for the given capability name.
        /// </summary>
        /// <param name="name">The capability name to look up.</param>
        /// <param name="handler">Output parameter for the handler if found.</param>
        /// <returns>True if a handler was found; otherwise false.</returns>
        public bool TryGetHandler(string name, out CapabilityHandler handler)
        {
            return _handlers.TryGetValue(name, out handler);
        }

        /// <summary>
        /// Returns a copy of all registered capability names as a string array.
        /// </summary>
        public string[] GetCapabilityNames()
        {
            string[] names = new string[_handlers.Count];
            int i = 0;
            foreach (KeyValuePair<string, CapabilityHandler> entry in _handlers)
            {
                names[i] = entry.Key;
                i++;
            }
            return names;
        }
    }
}
