using System.Collections.Concurrent;
using EventTicketing.Notifications.Email;
using MimeKit;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>An SMTP server that records what it is sent, and can be made unreachable for one recipient.</summary>
internal sealed class FakeMailServer : IEmailSender
{
    private readonly ConcurrentQueue<MimeMessage> _sent = new();
    private readonly ConcurrentDictionary<string, bool> _unreachableFor = new(StringComparer.OrdinalIgnoreCase);

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var recipient = message.To.Mailboxes.Single().Address;
        if (_unreachableFor.ContainsKey(recipient))
        {
            throw new IOException("The SMTP server can't be reached.");
        }

        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<MimeMessage> SentTo(string recipient) =>
        [.. _sent.Where(message => message.To.Mailboxes.Any(mailbox => string.Equals(mailbox.Address, recipient, StringComparison.OrdinalIgnoreCase)))];

    public void MakeUnreachableFor(string recipient) => _unreachableFor[recipient] = true;

    public void MakeReachableFor(string recipient) => _unreachableFor.TryRemove(recipient, out _);
}
