namespace Webhook

/// Core domain model for the payment webhook.
///
/// Everything in this module is immutable data. There are no functions with
/// side effects here, keeping the domain pure and trivial to reason about/test.
module Domain =

    /// The raw payload as received from the gateway. Every field is optional
    /// because we cannot trust an external system to send a well-formed body;
    /// validation later turns this loose shape into a trustworthy `Payment`.
    type RawPayload =
        { Event: string option
          TransactionId: string option
          Amount: string option
          Currency: string option
          Timestamp: string option }

    /// A fully validated payment. Reaching this type is proof that the payload
    /// passed every integrity, authenticity and business-rule check.
    type Payment =
        { Event: string
          TransactionId: string
          Amount: decimal
          Currency: string
          Timestamp: string }

    /// All the ways a webhook request can be rejected.
    ///
    /// `InvalidToken` is special: a forged request must be silently ignored
    /// (never cancelled), because we cannot trust anything it claims, including
    /// the transaction id.
    type WebhookError =
        | InvalidToken
        | InvalidPayload
        | MissingTransactionId
        | MissingField of field: string
        | DuplicateTransaction of transactionId: string
        | AmountMismatch of transactionId: string
