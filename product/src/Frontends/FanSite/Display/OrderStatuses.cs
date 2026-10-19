namespace EventTicketing.FanSite.Display;

/// <summary>The order statuses PublicApi reports (GET /orders/{id}), as the return page explains them.</summary>
internal static class OrderStatuses
{
    /// <summary>Still moving: the return page keeps asking. Pending waits for the payment, Refunding for the refund.</summary>
    public static bool IsMoving(string? status) => status is "Pending" or "Refunding";

    public static bool IsConfirmed(string? status) => status == "Confirmed";

    public static (string Headline, string Explanation) Describe(string? status, string? refundReason) => status switch
    {
        "Pending" => ("Waiting for your payment",
            "We confirm the order when the payment provider tells us the payment went through. This page updates by itself."),
        "Confirmed" => ("Your order is confirmed", "The payment went through and the seats are yours."),
        "Expired" => ("The time to pay ran out",
            "The payment was cancelled and the seats were released. You haven't been charged."),
        "Failed" => ("The payment was declined", "The seats were released. You haven't been charged."),
        "Refunding" => ("We're refunding your payment", RefundExplanation(refundReason)),
        "Refunded" => ("Your payment was refunded", RefundExplanation(refundReason)),
        "RefundFailed" => ("We couldn't refund your payment yet", "Someone from our team will sort it out and contact you."),
        _ => ("Checking your order", string.Empty),
    };

    private static string RefundExplanation(string? reason) => reason switch
    {
        "SeatsLost" => "The payment arrived after your hold had expired and someone else took a seat, so the money goes back to you.",
        "PaidAfterClose" => "The payment arrived after the time to pay had run out, so the money goes back to you.",
        _ => "The money goes back to you.",
    };
}
