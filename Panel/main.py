import os
import json
import base64
from datetime import datetime
from typing import Dict, Any, Optional, Generator
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Depends, Query, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse
from pydantic import BaseModel
import psycopg2
from psycopg2.extras import RealDictCursor
import clickhouse_connect


# ==========================================
# خواندن تنظیمات از متغیرهای محیطی
# ==========================================
CLICKHOUSE_HOST = os.getenv("CLICKHOUSE_HOST", "clickhouse")
CLICKHOUSE_PORT = int(os.getenv("CLICKHOUSE_PORT", "8123"))
CLICKHOUSE_USER = os.getenv("CLICKHOUSE_USER", "default")
CLICKHOUSE_PASSWORD = os.getenv("CLICKHOUSE_PASSWORD", "Root_2118908")
CLICKHOUSE_DB = os.getenv("CLICKHOUSE_DB", "Monitoring")

PG_HOST = os.getenv("PG_HOST", "postgres")
PG_PORT = int(os.getenv("PG_PORT", "5432"))
PG_USER = os.getenv("PG_USER", "postgres")
PG_PASSWORD = os.getenv("PG_PASSWORD", "Root_2118908")
PG_DATABASE = os.getenv("PG_DATABASE", "Monitoring")

UPDATE_DIR = "/app/Updates"


# ==========================================
# کلاینت‌های دیتابیس و مدیریت Lifecycle
# ==========================================
def get_clickhouse_client():
    try:
        return clickhouse_connect.get_client(
            host=CLICKHOUSE_HOST,
            port=CLICKHOUSE_PORT,
            username=CLICKHOUSE_USER,
            password=CLICKHOUSE_PASSWORD,
            database=CLICKHOUSE_DB,
        )
    except Exception as e:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=f"ClickHouse Connection Error: {str(e)}"
        )


def init_clickhouse_tables():
    """ایجاد ساختار اولیه جدول در ClickHouse در صورت عدم وجود"""
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
        print(f"[ERROR] Failed to ensure UpdateLogs table in ClickHouse: {ex}")


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup: ساخت جداول ClickHouse
    init_clickhouse_tables()
    yield
    # Shutdown logic (اگر نیاز بود)


app = FastAPI(
    title="Monitoring Dashboard API",
    description="API for fetching metrics, logs, updates, and managing settings",
    lifespan=lifespan
)

# تنظیم CORS
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


def get_pg_connection() -> Generator:
    """Dependency برای تولید و بستن خودکار کانکشن PostgreSQL"""
    conn = None
    try:
        conn = psycopg2.connect(
            host=PG_HOST,
            port=PG_PORT,
            user=PG_USER,
            password=PG_PASSWORD,
            dbname=PG_DATABASE
        )
        yield conn
    except Exception as e:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=f"PostgreSQL Connection Error: {str(e)}"
        )
    finally:
        if conn and not conn.closed:
            conn.close()


# ==========================================
# مدل‌ها و API های به‌روزرسانی (Updates)
# ==========================================
class UpdateLogPayload(BaseModel):
    computer_name: Optional[str] = "UNKNOWN"
    version: str
    status: str
    exit_code: int = 0
    source_dir: Optional[str] = ""
    target_dir: Optional[str] = ""
    log_details: Optional[str] = ""


class UpdateSettingsPayload(BaseModel):
    Update: Dict[str, Any]


@app.get("/api/updates/{filename}")
def download_update_package(filename: str):
    file_path = os.path.join(UPDATE_DIR, filename)
    safe_base = os.path.abspath(UPDATE_DIR)
    target_path = os.path.abspath(file_path)

    # جلوگیری از Directory Traversal
    if not target_path.startswith(safe_base) or not os.path.exists(target_path):
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Update package not found")

    return FileResponse(
        path=target_path,
        filename=filename,
        media_type="application/zip"
    )


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
        client.insert(
            table="UpdateLogs",
            data=data,
            column_names=[
                "Timestamp", "ComputerName", "Version", "Status",
                "ExitCode", "SourceDir", "TargetDir", "LogDetails"
            ]
        )
        return {"success": True, "message": "Update log recorded successfully"}
    except Exception as e:
        raise HTTPException(status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, detail=f"ClickHouse insert error: {str(e)}")
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


# ==========================================
# متریک‌ها و لاگ‌ها (ClickHouse)
# ==========================================
@app.get("/api/system-metrics")
def get_system_metrics(limit: int = 1000):
    client = get_clickhouse_client()
    try:
        query = f"SELECT * FROM SystemMetrics ORDER BY CollectedAt DESC LIMIT {limit}"
        result = client.query(query)
        return [dict(zip(result.column_names, row)) for row in result.result_rows]
    finally:
        client.close()


@app.get("/api/app-logs")
def get_app_logs(limit: int = 1000):
    client = get_clickhouse_client()
    try:
        query = f"SELECT * FROM AppLogs ORDER BY CollectedAt DESC LIMIT {limit}"
        result = client.query(query)
        return [dict(zip(result.column_names, row)) for row in result.result_rows]
    finally:
        client.close()


@app.get("/api/UserActivities")
def get_user_activities(limit: int = 1000):
    client = get_clickhouse_client()
    try:
        query = f"SELECT * FROM UserActivities ORDER BY Timestamp DESC LIMIT {limit}"
        result = client.query(query)
        return [dict(zip(result.column_names, row)) for row in result.result_rows]
    finally:
        client.close()


@app.get("/api/logs-chart")
def get_logs_chart():
    client = get_clickhouse_client()
    try:
        query = "SELECT LogLevel, count() as Count FROM AppLogs GROUP BY LogLevel"
        result = client.query(query)
        return [dict(zip(["LogLevel", "Count"], row)) for row in result.result_rows]
    finally:
        client.close()


# ==========================================
# تنظیمات و دستورات (PostgreSQL)
# ==========================================
@app.get("/api/images")
def get_alarm_images():
    conn = get_pg_connection()
    try:
        cur = conn.cursor()
        cur.execute("SELECT * FROM alarm_images")
        return [{"name": r[0], "data": base64.b64encode(r[1]).decode('utf-8')} for r in cur.fetchall() if r[1]]
    finally:
        cur.close()
        conn.close()


@app.get("/api/settings")
def get_settings(conn=Depends(get_pg_connection)):
    with conn.cursor() as cur:
        cur.execute("SELECT settings_json FROM app_configuration ORDER BY id DESC LIMIT 1")
        row = cur.fetchone()
        return row[0] if row else {}


@app.post("/api/settings")
def save_settings(settings: dict):
    conn = get_pg_connection()
    try:
        cur = conn.cursor() 
        # استفاده از UPSERT بر روی کلید اصلی id
        query = """
        INSERT INTO app_configuration (id, settings_json, updated_at)
        VALUES (1, %s, NOW())
        ON CONFLICT (id) 
        DO UPDATE SET 
            settings_json = EXCLUDED.settings_json,
            updated_at = NOW();
        """
        cur.execute(query, (json.dumps(settings),))
        conn.commit()
        return {"status": "success", "message": "Settings updated successfully"}
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Database error: {str(e)}")
    finally:
        cur.close()
        conn.close()


@app.post("/api/settings/update")
def patch_update_settings(payload: UpdateSettingsPayload, conn=Depends(get_pg_connection)):
    """
    فقط بلوک Update را در تنظیمات موجود Merge می‌کند.
    """
    try:
        with conn.cursor() as cur:
            # ۱. خواندن وضعیت فعلی
            cur.execute("SELECT settings_json FROM app_configuration WHERE id = 1")
            row = cur.fetchone()
            
            # ۲. ادغام تنظیمات (Merge)
            current_settings = row[0] if row else {}
            # آپدیت کردن فقط بخش Update
            current_settings["Update"] = payload.Update
            
            # ۳. ذخیره‌سازی مجدد (Atomic Upsert)
            query = """
            INSERT INTO app_configuration (id, settings_json, updated_at)
            VALUES (1, %s, NOW())
            ON CONFLICT (id) 
            DO UPDATE SET 
                settings_json = EXCLUDED.settings_json,
                updated_at = NOW();
            """
            cur.execute(query, (json.dumps(current_settings),))
            conn.commit()
            
            return {"status": "success", "message": "Update block patched successfully"}
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Database error: {str(e)}")

@app.get("/api/commands")
def get_pending_commands(
    computer: Optional[str] = Query(None),
    user: Optional[str] = Query(None),
    conn=Depends(get_pg_connection)
):
    with conn.cursor() as cur:
        query = """
            SELECT id, command
            FROM client_commands
            WHERE status = 'pending'
              AND (
                  computer_name = %s
                  OR windows_username = %s
                  OR (computer_name IS NULL AND windows_username IS NULL)
              )
        """
        cur.execute(query, (computer, user))
        rows = cur.fetchall()

        commands_list = []
        for cmd_id, cmd_data in rows:
            command_string = json.dumps(cmd_data, ensure_ascii=False) if cmd_data else ""
            commands_list.append({
                "Id": cmd_id,
                "Command": command_string
            })
        return commands_list


@app.post("/api/commands/{cmd_id}/execute")
def mark_command_executed(cmd_id: int, conn=Depends(get_pg_connection)):
    with conn.cursor() as cur:
        cur.execute("UPDATE client_commands SET status = 'executed' WHERE id = %s", (cmd_id,))
        conn.commit()

        if cur.rowcount == 0:
            raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Command not found")

        return {"success": True, "message": f"Command {cmd_id} marked as executed"}
