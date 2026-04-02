using System.Collections.Concurrent;
using Estimator.Core.Models;
using Estimator.Core.Models.Options;
using Microsoft.Extensions.Options;

namespace Estimator.Api.Services
{
    public interface IEstimateSessionStore
    {
        WorkflowSession Create(string description, string? requestedSessionId = null);
        bool TryGet(string sessionId, out WorkflowSession? session);
        void Save(WorkflowSession session);
    }

    public sealed class InMemoryEstimateSessionStore : IEstimateSessionStore
    {
        private readonly ConcurrentDictionary<string, WorkflowSession> _sessions =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly TimeSpan _ttl;

        public InMemoryEstimateSessionStore(IOptions<AiSettings> options)
        {
            _ttl = TimeSpan.FromMinutes(Math.Max(30, options.Value.SessionTtlMinutes));
        }

        public WorkflowSession Create(string description, string? requestedSessionId = null)
        {
            CleanupExpired();

            var sessionId = string.IsNullOrWhiteSpace(requestedSessionId)
                ? Guid.NewGuid().ToString("N")
                : requestedSessionId.Trim();

            var session = new WorkflowSession
            {
                SessionId = sessionId,
                ProjectDescription = description.Trim(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _sessions[sessionId] = session;
            return session;
        }

        public bool TryGet(string sessionId, out WorkflowSession? session)
        {
            session = null;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            CleanupExpired();
            if (!_sessions.TryGetValue(sessionId.Trim(), out var existing))
            {
                return false;
            }

            existing.UpdatedAtUtc = DateTime.UtcNow;
            session = existing;
            return true;
        }

        public void Save(WorkflowSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            session.UpdatedAtUtc = DateTime.UtcNow;
            _sessions[session.SessionId] = session;
        }

        private void CleanupExpired()
        {
            var now = DateTime.UtcNow;
            foreach (var pair in _sessions)
            {
                if (now - pair.Value.UpdatedAtUtc > _ttl)
                {
                    _sessions.TryRemove(pair.Key, out _);
                }
            }
        }
    }
}
