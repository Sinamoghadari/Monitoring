from fastapi import FastAPI, HTTPException , Depends , Query
from sqlalchemy.orm import Session
from sqlalchemy import text
from fastapi.middleware.cors import CORSMiddleware
import clickhouse_connect
import psycopg2
import json
from typing import Dict, Any
import base64
import os



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
CLICKHOUSE_HOST = os.getenv('CLICKHOUSE_HOST', '172.17.214.28')
CLICKHOUSE_PORT = int(os.getenv('CLICKHOUSE_PORT', 8123))
CLICKHOUSE_USER = os.getenv('CLICKHOUSE_USER', 'default')
CLICKHOUSE_PASSWORD = os.getenv('CLICKHOUSE_PASSWORD', 'Root_2118908')
DATABASE = os.getenv('CLICKHOUSE_DB', 'Monitoring')

# --- تنظیمات PostgreSQL ---
PG_HOST = os.getenv('PG_HOST', '172.17.214.28')
PG_PORT = int(os.getenv('PG_PORT', 6543))
PG_USER = os.getenv('PG_USER', 'postgres')
PG_PASSWORD = os.getenv('PG_PASSWORD', 'Root_2118908')
PG_DATABASE = os.getenv('PG_DATABASE', 'Monitoring')

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


@app.get("/api/images")
def get_alarm_images(db: Session = Depends(get_pg_connection)):
    conn = get_pg_connection()
    try :
        cur = conn.cursor()
        cur.execute("SELECT * FROM alarm_images")
        result = cur.fetchall()
        

        images_list = []
        for row in result:
            if row[1]: 
                base64_encoded = base64.b64encode(row[1]).decode('utf-8')
                images_list.append({
                    "name": row[0],
                    "data": base64_encoded
                })  
        return images_list
    
    finally:
        cur.close()
        conn.close()


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
        query = f"SELECT * FROM AppLogs ORDER BY CollectedAt DESC LIMIT {limit}"
        result = client.query(query)
        columns = result.column_names
        return [dict(zip(columns, row)) for row in result.result_rows]
    finally:
        client.close()


@app.get("/api/UserActivities")
def get_user_activities(limit: int = 1000):
    client = get_clickhouse_client()
    try:
        query = f"SELECT * FROM UserActivities ORDER BY CollectedAt DESC LIMIT {limit}"
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

# ==========================================
# API های مربوط به دستورات ریموت (Commands)
# ==========================================

@app.get("/api/commands")
def get_pending_commands(computer: str = Query(None), user: str = Query(None)):
    conn = get_pg_connection()
    try:
        commands_list = []
        cur = conn.cursor()
        # فقط دستوراتی را بگیر که اجرا نشده‌اند
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
        for row in rows:
            cmd_id = row[0]
            cmd_data = row[1] # به دلیل تغییر SELECT، اندیس فیلد command تغییر کرد
            
            # تبدیل به رشته JSON برای کلاس سی‌شارپ
            command_string = json.dumps(cmd_data, ensure_ascii=False) if cmd_data else ""
            
            commands_list.append({
                "Id": cmd_id,
                "Command": command_string  
            })
            
        return commands_list
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))
    
    finally:
        if 'conn' in locals() and conn:
            conn.close()

@app.post("/api/commands/{cmd_id}/execute")
def mark_command_executed(cmd_id: int):
    """علامت‌گذاری دستور به عنوان اجرا شده پس از موفقیت در کلاینت"""
    conn = get_pg_connection()
    try:
        cur = conn.cursor()
        # نام جدول اصلاح شد و آپدیت روی فیلد status انجام می‌شود
        cur.execute("UPDATE client_commands SET status = 'executed' WHERE id = %s", (cmd_id,))
        conn.commit()
        
        # اگر هیچ رکوردی آپدیت نشد
        if cur.rowcount == 0:
            raise HTTPException(status_code=404, detail="Command not found")
            
        return {"success": True, "message": f"Command {cmd_id} marked as executed"}
    except Exception as e:
         raise HTTPException(status_code=500, detail=str(e))
    finally:
        if 'conn' in locals() and conn:
            conn.close()

