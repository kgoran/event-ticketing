using System.Globalization;
using System.Text.Encodings.Web;

namespace EventTicketing.Tooling.LocalPay;

public sealed class CheckoutOptions
{
    /// <summary>The shop's name on the page, as a real provider shows the merchant's (configuration "Checkout:ShopName").</summary>
    public string ShopName { get; set; } = "the shop";
}

/// <summary>
/// The hosted payment page: Approve or Decline while the payment is open, and a way back to the
/// shop without deciding. Going back leaves the payment open until it expires, as with a real
/// provider's hosted checkout.
/// </summary>
public static class CheckoutPage
{
    public static string Render(Payment payment, string shopName)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var encode = HtmlEncoder.Default;
        var amount = (payment.AmountMinorUnits / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        var id = encode.Encode(payment.Id);
        var open = payment.Status == PaymentStatus.RequiresPayment;
        var scenario = Scenarios.Describe(payment.Scenario);

        // Only a web address becomes a link: the shop chose the return URL, and a "javascript:" one
        // would run in this page.
        var backToShop = Uri.TryCreate(payment.ReturnUrl, UriKind.Absolute, out var returnUrl) && (returnUrl.Scheme == Uri.UriSchemeHttp || returnUrl.Scheme == Uri.UriSchemeHttps)
            ? $"""<p class="back"><a class="button" href="{encode.Encode(returnUrl.AbsoluteUri)}">Back to {encode.Encode(shopName)}</a>{(open ? "<small>without paying: the payment stays open until it expires</small>" : string.Empty)}</p>"""
            : string.Empty;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>LocalPay checkout</title>
              <style>
                body { font-family: system-ui, sans-serif; max-width: 26rem; margin: 3rem auto; padding: 0 1rem; color: #1b1b1f; }
                .amount { font-size: 2rem; margin: .5rem 0 1.5rem; }
                form { display: inline; }
                button, .button { font-size: 1rem; padding: .6rem 1.4rem; margin-right: .5rem; border-radius: .4rem; border: 1px solid #888; cursor: pointer; background: white; color: inherit; text-decoration: none; display: inline-block; }
                .back { margin-top: 1.5rem; }
                .back small { display: block; margin-top: .4rem; }
                .approve { background: #1f7a3a; color: white; border-color: #1f7a3a; }
                .scenario { background: #fff4d6; border: 1px solid #e0c060; padding: .5rem .75rem; border-radius: .4rem; }
                small { color: #666; }
              </style>
            </head>
            <body>
              <h1>LocalPay</h1>
              <p>Payment for order <code>{{encode.Encode(payment.Reference)}}</code></p>
              <div class="amount">{{amount}} {{encode.Encode(payment.Currency)}}</div>
              {{(scenario.Length > 0 ? $"<p class=\"scenario\">Test scenario: {encode.Encode(scenario)}</p>" : string.Empty)}}
              {{(open
                  ? $"""
                    <form method="post" action="/checkout/{id}/approve"><button class="approve">Approve</button></form>
                    <form method="post" action="/checkout/{id}/decline"><button>Decline</button></form>
                    """
                  : $"<p>This payment is <strong>{encode.Encode(payment.Status)}</strong>.</p>")}}
              {{backToShop}}
              <p><small>A payment simulator for development and tests. No real money moves.</small></p>
            </body>
            </html>
            """;
    }
}
