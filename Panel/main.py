from fastapi import FastAPI, HTTPException, Depends, Query
from sqlalchemy.orm import Session
from sqlalchemy import text
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import clickhouse_connect
import psycopg2
import json
from typing import Dict, Any, Optional
import base64
import os
from datetime import datetime
from fastapi.responses import FileResponse

app = FastAPI(
    title="Monitoring Dashboard API",
    description="API for fetching metrics, logs, and managing settings"
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

CLICKHOUSE_HOST = os.getenv('CLICKHOUSE_HOST', '172.17.214.38')
CLICKHOUSE_PORT = int(os.getenv('CLICKHOUSE_PORT', 8123))
CLICKHOUSE_USER = os.getenv('CLICKHOUSE_USER', 'default')
CLICKHOUSE_PASSWORD = os.getenv('CLICKHOUSE_PASSWORD', 'Root_2118908')
DATABASE = os.getenv('CLICKHOUSE_DB', 'Monitoring')

PG_HOST = os.getenv('PG_HOST', '172.17.214.38')
PG_PORT = int(os.getenv('PG_PORT', 6543))
PG_USER = os.getenv('PG_USER', 'postgres')
PG_PASSWORD = os.getenv('PG_PASSWORD', 'Root_2118908')
PG_DATABASE = os.getenv('PG_DATABASE', 'Monitoring')

UPDATE_DIR = "/app/Updates"


def get_clickhouse_client():
    try:
        return clickhouse_connect.get_client(
            host=CLICKHOUSE_HOST,
            port=CLICKHOUSE_PORT,
            username=CLICKHOUSE_USER,
            password=CLICKHOUSE_PASSWORD,
            database=DATABASE
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"ClickHouse Connection Error: {str(e)}")


def init_clickhouse_tables():
    try:
        client = get_clickhouse_client()
        query = """
        CREATE TABLE IF NOT EXISTS UpdateLogs (
            Timestamp DateTime DEFAULT now(),
            ComputerName String,
            Version String,
            Status String,
            ExitCode Int32,
            SourceDir String,
            TargetDir String,
            LogDetails String
        ) ENGINE = MergeTree()
        ORDER BY (Timestamp, ComputerName, Version)
        """
        client.command(query)
        client.close()
    except Exception as ex:
        print(f"Failed to ensure UpdateLogs table exists: {ex}")


@app.on_event("startup")
def on_startup():
    init_clickhouse_tables()


def get_pg_connection():
    try:
        return psycopg2.connect(
            host=PG_HOST,
            port=PG_PORT,
            user=PG_USER,
            password=PG_PASSWORD,
            dbname=PG_DATABASE
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"PostgreSQL Connection Error: {str(e)}")


class UpdateLogPayload(BaseModel):
    computer_name: Optional[str] = "UNKNOWN"
    version: str
    status: str
    exit_code: int = 0
    source_dir: Optional[str] = ""
    target_dir: Optional[str] = ""
    log_details: Optional[str] = ""


@app.get("/api/updates/{filename}")
def download_update_package(filename: str):
    file_path = os.path.join(UPDATE_DIR, filename)
    if not os.path.abspath(file_path).startswith(UPDATE_DIR) or not os.path.exists(file_path):
        raise HTTPException(status_code=404, detail="Update package not found")
    return FileResponse(path=file_path, filename=filename, media_type="application/zip")


@app.post("/api/updates/report")
def receive_update_report(payload: UpdateLogPayload):
    client = get_clickhouse_client()
    try:
        data = [[
            datetime.utcnow(),
            payload.computer_name,
            payload.version,
            payload.status,
            payload.exit_code,
            payload.source_dir,
            payload.target_dir,
            payload.log_details
        ]]
        client.insert('UpdateLogs', data, column_names=[
            'Timestamp', 'ComputerName', 'Version', 'Status', 'ExitCode', 'SourceDir', 'TargetDir', 'LogDetails'
        ])
        return {"success": True, "message": "Update log recorded successfully"}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to insert update log: {str(e)}")
    finally:
        client.close()


@app.get("/api/update-logs")
def get_update_logs(limit: int = 100):
    client = get_clickhouse_client()
    try:
        query = f"SELECT * FROM UpdateLogs ORDER BY Timestamp DESC LIMIT {limit}"
        result = client.query(query)
        return [dict(zip(result.column_names, row)) for row in result.result_rows]
    finally:
        client.close()


@app.get("/api/images")
def get_alarm_images(db: Session = Depends(get_pg_connection)):
    conn = get_pg_connection()
    try:
        cur = conn.cursor()
        cur.execute("SELECT * FROM alarm_images")
        return [{"name": r[0], "data": base64.b64encode(r[1]).decode('utf-8')} for r in cur.fetchall() if r[1]]
    finally:
        cur.close()
        conn.close()


@app.get("/api/system-metrics")
def get_system_metrics(limit: int = 1000):
    client = get_clickhouse_client()
    try:
        res = client.query(f"SELECT * FROM SystemMetrics ORDER BY CollectedAt DESC LIMIT {limit}")
        return [dict(zip(res.column_names, row)) for row in res.result_rows]
    finally:
        client.close()


@app.get("/api/app-logs")
def get_app_logs(limit: int = 1000):
    client = get_clickhouse_client()
    try:
        res = client.query(f"SELECT * FROM AppLogs ORDER BY CollectedAt DESC LIMIT {limit}")
        return [dict(zip(res.column_names, row)) for row in res.result_rows]
    finally:
        client.close()


@app.get("/api/UserActivities")
def get_user_activities(limit: int = 1000):
    client = get_clickhouse_client()
    try:
        res = client.query(f"SELECT * FROM UserActivities ORDER BY Timestamp DESC LIMIT {limit}")
        return [dict(zip(res.column_names, row)) for row in res.result_rows]
    finally:
        client.close()


@app.get("/api/settings")
def get_settings():
    conn = get_pg_connection()
    try:
        cur = conn.cursor()
        cur.execute("SELECT settings_json FROM app_configuration ORDER BY id DESC LIMIT 1")
        row = cur.fetchone()
        return row[0] if row else {}
    finally:
        conn.close()


@app.post("/api/settings")
def save_settings(settings: Dict[str, Any]):
    conn = get_pg_connection()
    try:
        cur = conn.cursor()
        cur.execute("INSERT INTO app_configuration (settings_json) VALUES (%s)", (json.dumps(settings),))
        conn.commit()
        return {"success": True, "message": "Settings saved"}
    finally:
        conn.close()


@app.get("/api/logs-chart")
def get_logs_chart():
    client = get_clickhouse_client()
    try:
        res = client.query("SELECT LogLevel, count() as Count FROM AppLogs GROUP BY LogLevel")
        return [dict(zip(["LogLevel", "Count"], row)) for row in res.result_rows]
    finally:
        client.close()


@app.get("/api/commands")
def get_pending_commands(computer: str = Query(None), user: str = Query(None)):
    conn = get_pg_connection()
    try:
        cur = conn.cursor()
        cur.execute("""
            SELECT id, command FROM client_commands
            WHERE status = 'pending'
              AND (computer_name = %s OR windows_username = %s OR (computer_name IS NULL AND windows_username IS NULL))
        """, (computer, user))
        return [{"Id": r[0], "Command": json.dumps(r[1], ensure_ascii=False) if r[1] else ""} for r in cur.fetchall()]
    finally:
        conn.close()


@app.post("/api/commands/{cmd_id}/execute")
def mark_command_executed(cmd_id: int):
    conn = get_pg_connection()
    try:
        cur = conn.cursor()
        cur.execute("UPDATE client_commands SET status = 'executed' WHERE id = %s", (cmd_id,))
        conn.commit()
        if cur.rowcount == 0:
            raise HTTPException(status_code=404, detail="Command not found")
        return {"success": True, "message": f"Command {cmd_id} marked as executed"}
    finally:
        conn.close()
