from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
import clickhouse_connect
import psycopg2
import json
from typing import Dict, Any

app = FastAPI(
    title="Monitoring Dashboard API",
    description="API for fetching metrics, logs, and managing settings"
)

# --- رفع خطای CORS برای اتصال مرورگر ---
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"], 
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# --- تنظیمات ClickHouse ---
CLICKHOUSE_HOST = '172.17.214.28'
CLICKHOUSE_PORT = 8123  
CLICKHOUSE_USER = 'default'
CLICKHOUSE_PASSWORD = 'Root_2118908'
DATABASE = "Monitoring"

# --- تنظیمات PostgreSQL ---
PG_HOST = '172.17.214.28'
PG_PORT = 6543
PG_USER = 'postgres'
PG_PASSWORD = 'Root_2118908'
PG_DATABASE = "Monitoring"


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

# ==========================================
# API های Power BI (کدهای قبلی شما)
# ==========================================
@app.get("/api/system-metrics")
def get_system_metrics(limit: int = 1000):
    client = get_clickhouse_client()
    try:
        query = f"SELECT * FROM SystemMetrics ORDER BY CollectedAt DESC LIMIT {limit}"
        result = client.query(query)
        columns = result.column_names
        return [dict(zip(columns, row)) for row in result.result_rows]
    finally:
        client.close()

@app.get("/api/app-logs")
def get_app_logs(limit: int = 1000):
    client = get_clickhouse_client()
    try:
        query = f"SELECT * FROM AppLogs ORDER BY Timestamp DESC LIMIT {limit}"
        result = client.query(query)
        columns = result.column_names
        return [dict(zip(columns, row)) for row in result.result_rows]
    finally:
        client.close()

# ==========================================
# API های پنل مدیریت وب (تنظیمات Postgres)
# ==========================================
@app.get("/api/settings")
def get_settings():
    """خواندن آخرین تنظیمات از PostgreSQL"""
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
    """درج تنظیمات جدید در PostgreSQL"""
    conn = get_pg_connection()
    try:
        cur = conn.cursor()
        settings_str = json.dumps(settings)
        cur.execute("INSERT INTO app_configuration (settings_json) VALUES (%s)", (settings_str,))
        conn.commit()
        return {"success": True, "message": "Settings saved"}
    finally:
        conn.close()

# ==========================================
# API نمودار برای داشبورد وب
# ==========================================
@app.get("/api/logs-chart")
def get_logs_chart():
    """خواندن تعداد لاگ‌ها بر اساس سطح برای نمودار"""
    client = get_clickhouse_client()
    try:
        query = "SELECT LogLevel, count() as Count FROM AppLogs GROUP BY LogLevel"
        result = client.query(query)
        columns = ["LogLevel", "Count"]
        return [dict(zip(columns, row)) for row in result.result_rows]
    finally:
        client.close()
