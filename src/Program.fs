module Webhook.Program

open System
open System.Text.Json
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Giraffe
open Webhook.Domain

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

/// Shared secret used to authenticate the gateway. Overridable via env var so
/// the real secret never has to live in source control.
let private secretToken =
    match Environment.GetEnvironmentVariable "WEBHOOK_TOKEN" with
    | null | "" -> "meu-token-secreto"
    | t -> t

let private httpPort = 5000
let private httpsPort = 5443

// ---------------------------------------------------------------------------
// JSON parsing (boundary between the untrusted outside world and our domain)
// ---------------------------------------------------------------------------

/// Parses the request body into a loose `RawPayload`. Returns `None` when the
/// body is not a JSON object. `amount` is accepted both as a JSON string
/// ("49.90") and as a JSON number (49.90) so we interoperate with any gateway.
let parseRawPayload (body: string) : RawPayload option =
    try
        use doc = JsonDocument.Parse body
        let root = doc.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            None
        else
            let getField (name: string) =
                match root.TryGetProperty name with
                | true, el ->
                    match el.ValueKind with
                    | JsonValueKind.String -> Some(el.GetString())
                    | JsonValueKind.Null -> None
                    | _ -> Some(el.GetRawText())
                | _ -> None

            Some
                { Event = getField "event"
                  TransactionId = getField "transaction_id"
                  Amount = getField "amount"
                  Currency = getField "currency"
                  Timestamp = getField "timestamp" }
    with _ ->
        None

// ---------------------------------------------------------------------------
// HTTP responses
// ---------------------------------------------------------------------------

let private confirmedBody (payment: Payment) : obj =
    {| status = "confirmed"
       transaction_id = payment.TransactionId |}

let private errorBody (error: WebhookError) : obj =
    match error with
    | DuplicateTransaction id
    | AmountMismatch id ->
        {| status = "cancelled"
           transaction_id = id
           reason = Validation.reason error |}
    | _ ->
        {| status = "cancelled"
           reason = Validation.reason error |}

let private respondError (error: WebhookError) : HttpHandler =
    setStatusCode (Validation.statusCode error) >=> json (errorBody error)

let private respondConfirmed (payment: Payment) : HttpHandler =
    setStatusCode 200 >=> json (confirmedBody payment)

// ---------------------------------------------------------------------------
// Side-effecting helpers (kept small and explicit)
// ---------------------------------------------------------------------------

let private cancel (transactionId: string) =
    Async.StartAsTask(Gateway.cancel transactionId)

let private confirm (transactionId: string) =
    Async.StartAsTask(Gateway.confirm transactionId)

// ---------------------------------------------------------------------------
// The webhook handler: composes validation (pure) with effects (impure)
// ---------------------------------------------------------------------------

let webhookHandler: HttpHandler =
    fun next ctx ->
        task {
            let token = ctx.TryGetRequestHeader "X-Webhook-Token"

            match Validation.validateToken secretToken token with
            // A forged request is silently ignored: respond, but never cancel.
            | Error err -> return! respondError err next ctx
            | Ok() ->
                let! body = ctx.ReadBodyFromRequestAsync()

                match parseRawPayload body with
                | None -> return! respondError InvalidPayload next ctx
                | Some raw ->
                    match Validation.validateStructure raw with
                    | Error err ->
                        if Validation.shouldCancel err then
                            match raw.TransactionId with
                            | Some id -> do! cancel id
                            | None -> ()

                        return! respondError err next ctx
                    | Ok txId ->
                        // Idempotency: a transaction confirmed once is never
                        // confirmed again; a replay is cancelled instead.
                        if Database.isConfirmed txId then
                            do! cancel txId
                            return! respondError (DuplicateTransaction txId) next ctx
                        else
                            match Validation.validateOrder raw txId with
                            | Error err ->
                                do! cancel txId
                                return! respondError err next ctx
                            | Ok payment ->
                                Database.save payment "confirmed"
                                do! confirm txId
                                return! respondConfirmed payment next ctx
        }

let webApp: HttpHandler =
    choose
        [ POST >=> route "/webhook" >=> webhookHandler
          GET >=> route "/health" >=> text "ok"
          setStatusCode 404 >=> text "not found" ]

// ---------------------------------------------------------------------------
// HTTPS: self-signed certificate generated in-memory at startup
// ---------------------------------------------------------------------------

let private createSelfSignedCertificate () : X509Certificate2 =
    use rsa = RSA.Create 2048
    let request =
        CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

    let sanBuilder = SubjectAlternativeNameBuilder()
    sanBuilder.AddDnsName "localhost"
    sanBuilder.AddIpAddress(Net.IPAddress.Loopback)
    request.CertificateExtensions.Add(sanBuilder.Build())

    let cert =
        request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1.0), DateTimeOffset.UtcNow.AddYears(1))

    // Round-trip through PFX so Kestrel can access the private key on macOS.
    X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null)

[<EntryPoint>]
let main args =
    Database.init ()

    let builder = WebApplication.CreateBuilder(args)
    builder.Services.AddGiraffe() |> ignore
    builder.Logging.ClearProviders() |> ignore
    builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning) |> ignore

    let certificate = createSelfSignedCertificate ()

    builder.WebHost.ConfigureKestrel(fun options ->
        // Plain HTTP for the grading harness and local development.
        options.ListenLocalhost httpPort
        // HTTPS endpoint (optional requirement) with the in-memory certificate.
        options.ListenLocalhost(httpsPort, fun listen -> listen.UseHttps certificate |> ignore))
    |> ignore

    let app = builder.Build()
    app.UseGiraffe webApp

    printfn "Webhook listening on http://localhost:%d and https://localhost:%d" httpPort httpsPort
    app.Run()
    0
