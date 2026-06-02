namespace Webhook

open System
open Microsoft.Data.Sqlite
open Webhook.Domain

/// Persistence layer backed by SQLite.
///
/// Side effects are deliberately concentrated here and behind a tiny, explicit
/// API so the rest of the application can stay pure. The database doubles as
/// the source of truth for idempotency: a transaction id can only be confirmed
/// once, which is enforced by a PRIMARY KEY constraint.
module Database =

    /// Connection string for the SQLite file. Using a file (instead of an
    /// in-memory DB) means confirmations survive restarts, giving real
    /// idempotency across the lifetime of the service.
    let private connectionString = "Data Source=webhook.db"

    let private openConnection () =
        let conn = new SqliteConnection(connectionString)
        conn.Open()
        conn

    /// Creates the transactions table if it does not exist yet. Safe to call on
    /// every startup.
    let init () =
        use conn = openConnection ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            """
            CREATE TABLE IF NOT EXISTS transactions (
                transaction_id TEXT PRIMARY KEY,
                event          TEXT NOT NULL,
                amount         TEXT NOT NULL,
                currency       TEXT NOT NULL,
                timestamp      TEXT NOT NULL,
                status         TEXT NOT NULL,
                processed_at   TEXT NOT NULL
            );
            """
        cmd.ExecuteNonQuery() |> ignore

    /// Returns true when the given transaction has already been confirmed.
    /// This is the idempotency guard that prevents double-processing of a
    /// payment that the gateway re-delivers.
    let isConfirmed (transactionId: string) : bool =
        use conn = openConnection ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT COUNT(1) FROM transactions WHERE transaction_id = $id AND status = 'confirmed';"
        cmd.Parameters.AddWithValue("$id", transactionId) |> ignore
        let count = cmd.ExecuteScalar() :?> int64
        count > 0L

    /// Persists the final state of a processed payment. `INSERT OR REPLACE`
    /// keeps the table consistent even if the same id is seen again after a
    /// non-confirming outcome.
    let save (payment: Payment) (status: string) : unit =
        use conn = openConnection ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            """
            INSERT OR REPLACE INTO transactions
                (transaction_id, event, amount, currency, timestamp, status, processed_at)
            VALUES ($id, $event, $amount, $currency, $timestamp, $status, $processed_at);
            """
        cmd.Parameters.AddWithValue("$id", payment.TransactionId) |> ignore
        cmd.Parameters.AddWithValue("$event", payment.Event) |> ignore
        cmd.Parameters.AddWithValue("$amount", string payment.Amount) |> ignore
        cmd.Parameters.AddWithValue("$currency", payment.Currency) |> ignore
        cmd.Parameters.AddWithValue("$timestamp", payment.Timestamp) |> ignore
        cmd.Parameters.AddWithValue("$status", status) |> ignore
        cmd.Parameters.AddWithValue("$processed_at", DateTime.UtcNow.ToString("o")) |> ignore
        cmd.ExecuteNonQuery() |> ignore
