namespace Webhook

open System.Net.Http
open System.Text
open System.Text.Json

/// Outbound HTTP client used to notify the payment gateway about the outcome
/// of a transaction (confirm or cancel).
///
/// The gateway base URL defaults to the address used by the provided test
/// harness but can be overridden through the GATEWAY_URL environment variable.
module Gateway =

    let private gatewayUrl =
        match System.Environment.GetEnvironmentVariable "GATEWAY_URL" with
        | null | "" -> "http://127.0.0.1:5001"
        | url -> url

    // A single shared HttpClient is the recommended pattern; creating one per
    // request exhausts sockets under load.
    let private httpClient = new HttpClient()

    let private postJson (path: string) (transactionId: string) : Async<unit> =
        async {
            let body = JsonSerializer.Serialize {| transaction_id = transactionId |}
            use content = new StringContent(body, Encoding.UTF8, "application/json")
            try
                let! _ = httpClient.PostAsync($"{gatewayUrl}{path}", content) |> Async.AwaitTask
                return ()
            with _ ->
                // Network failures to the gateway must not crash the webhook;
                // the spec explicitly calls for tolerating delivery failures.
                return ()
        }

    /// Tells the gateway the payment is confirmed and should be settled.
    let confirm (transactionId: string) : Async<unit> = postJson "/confirmar" transactionId

    /// Tells the gateway the payment must be cancelled/rolled back.
    let cancel (transactionId: string) : Async<unit> = postJson "/cancelar" transactionId
