namespace Webhook

open System.Globalization
open Webhook.Domain

/// Pure validation logic, written in a railway-oriented style.
///
/// Every function is total and side-effect free: given the same input it
/// always returns the same `Result`. This makes the business rules trivial to
/// unit-test and lets us compose them with `Result.bind` into a single
/// pipeline. The only check that lives outside this module is the duplicate
/// detection, because it needs to query the database.
module Validation =

    /// Business expectation for this exercise: an order of R$ 49,90 in BRL.
    /// In a real system these would come from looking the order up in a store.
    let expectedAmount = 49.90m
    let expectedCurrency = "BRL"

    let private isPresent (value: string option) =
        match value with
        | Some v when v.Trim() <> "" -> true
        | _ -> false

    /// A forged request (wrong/absent token) is rejected as `InvalidToken`.
    /// This is the authenticity/veracity check.
    let validateToken (secret: string) (token: string option) : Result<unit, WebhookError> =
        match token with
        | Some t when t = secret -> Ok ()
        | _ -> Error InvalidToken

    /// The transaction id is required before anything else, and uniquely, its
    /// absence must NOT trigger a cancellation: without an id there is nothing
    /// to cancel and the request is treated as malformed noise.
    let private requireTransactionId (raw: RawPayload) : Result<string, WebhookError> =
        if isPresent raw.TransactionId then Ok(raw.TransactionId.Value)
        else Error MissingTransactionId

    /// Integrity check: every remaining mandatory field must be present.
    let private requireFields (raw: RawPayload) : Result<unit, WebhookError> =
        let fields =
            [ "event", raw.Event
              "amount", raw.Amount
              "currency", raw.Currency
              "timestamp", raw.Timestamp ]

        fields
        |> List.tryFind (fun (_, value) -> not (isPresent value))
        |> function
            | Some(name, _) -> Error(MissingField name)
            | None -> Ok ()

    /// Structural validation: transaction id present, then every other
    /// mandatory field present. Returns the trusted transaction id so the
    /// caller can run the (impure) duplicate check before the order check,
    /// exactly mirroring the reference processing order.
    let validateStructure (raw: RawPayload) : Result<string, WebhookError> =
        requireTransactionId raw
        |> Result.bind (fun txId -> requireFields raw |> Result.map (fun () -> txId))

    /// Builds a trusted `Payment` and verifies the business rule (correct
    /// amount and currency). Any divergence is an `AmountMismatch`.
    let validateOrder (raw: RawPayload) (txId: string) : Result<Payment, WebhookError> =
        let parsedAmount =
            match raw.Amount with
            | Some a ->
                match System.Decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture) with
                | true, value -> Some value
                | false, _ -> None
            | None -> None

        match parsedAmount with
        | Some amount when amount = expectedAmount && raw.Currency = Some expectedCurrency ->
            Ok
                { Event = raw.Event.Value
                  TransactionId = txId
                  Amount = amount
                  Currency = raw.Currency.Value
                  Timestamp = raw.Timestamp.Value }
        | _ -> Error(AmountMismatch txId)

    /// Decides whether a rejected request should result in a cancellation
    /// request to the gateway. Forged requests and id-less payloads are simply
    /// ignored, everything else is actively cancelled.
    let shouldCancel (error: WebhookError) : bool =
        match error with
        | InvalidToken
        | InvalidPayload
        | MissingTransactionId -> false
        | MissingField _
        | DuplicateTransaction _
        | AmountMismatch _ -> true

    /// Maps a domain error to the HTTP status code returned to the gateway.
    let statusCode (error: WebhookError) : int =
        match error with
        | InvalidToken -> 403
        | _ -> 400

    /// Human-readable reason surfaced in the JSON response body.
    let reason (error: WebhookError) : string =
        match error with
        | InvalidToken -> "invalid token"
        | InvalidPayload -> "invalid payload"
        | MissingTransactionId -> "missing field: transaction_id"
        | MissingField field -> sprintf "missing field: %s" field
        | DuplicateTransaction _ -> "transaction duplicated"
        | AmountMismatch _ -> "mismatch"
