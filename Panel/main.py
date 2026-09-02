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
CLICKHOUSE_HOST = os.getenv('CLICKHOUSE_HOST', '172.17.214.38')
CLICKHOUSE_PORT = int(os.getenv('CLICKHOUSE_PORT', 8123))
CLICKHOUSE_USER = os.getenv('CLICKHOUSE_USER', 'default')
CLICKHOUSE_PASSWORD = os.getenv('CLICKHOUSE_PASSWORD', 'Root_2118908')
DATABASE = os.getenv('CLICKHOUSE_DB', 'Monitoring')

# --- تنظیمات PostgreSQL ---
PG_HOST = os.getenv('PG_HOST', '172.17.214.38')
PG_PORT = int(os.getenv('PG_PORT', 6543))
PG_USER = os.getenv('PG_USER', 'postgres')
PG_PASSWORD = os.getenv('PG_PASSWORD', 'Root_2118908')
PG_DATABASE = os.getenv('PG_DATABASE', 'Monitoring')


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

@app.get("/api/agent-update")
def get_agent_update():
    """خواندن مانیفست به‌روزرسانی عامل از آخرین تنظیمات PostgreSQL"""
    conn = get_pg_connection()
    try:
        cur = conn.cursor()
        cur.execute("SELECT settings_json FROM app_configuration ORDER BY id DESC LIMIT 1")
        row = cur.fetchone()
        if not row or row[0] is None:
            return {"Enabled": False}
        settings = row[0]
        if isinstance(settings, str):
            settings = json.loads(settings)
        update = settings.get("Update") or settings.get("update") or {"Enabled": False}
        return update
    finally:
        conn.close()

@app.post("/api/updates/report")
def report_agent_update(payload: Dict[str, Any]):
    """ثبت نتیجه اعمال به‌روزرسانی عامل (از apply_update.bat)."""
    conn = get_pg_connection()
    try:
        cur = conn.cursor()
        cur.execute(
            """
            CREATE TABLE IF NOT EXISTS agent_update_reports (
                id SERIAL PRIMARY KEY,
                computer_name TEXT,
                version TEXT,
                status TEXT,
                exit_code INTEGER,
                source_dir TEXT,
                target_dir TEXT,
                log_details TEXT,
                reported_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            )
            """
        )
        cur.execute(
            """
            INSERT INTO agent_update_reports
                (computer_name, version, status, exit_code, source_dir, target_dir, log_details)
            VALUES (%s, %s, %s, %s, %s, %s, %s)
            """,
            (
                payload.get("computer_name"),
                payload.get("version"),
                payload.get("status"),
                payload.get("exit_code"),
                payload.get("source_dir"),
                payload.get("target_dir"),
                payload.get("log_details"),
            ),
        )
        conn.commit()
        return {"success": True}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))
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

root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# ls
Dockerfile  main.py  requirements.txt
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# cat Dockerfile
# استفاده از ایمیج سبک پایتون
FROM python:3.12-slim

# تنظیم پوشه کاری داخل کانتینر
WORKDIR /app

# کپی کردن فایل نیازمندی‌ها و نصب آن‌ها
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# کپی کردن کل کدهای پروژه به داخل کانتینر
COPY . .

# باز کردن پورت 8000
EXPOSE 8000

# دستور اجرای برنامه (فرض بر این است که فایل اصلی main.py و نام اپ app است)
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# nano Dockerfile
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# ls
Dockerfile  main.py  requirements.txt
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel#
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# cd ..
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# ls
assets  database_data  docker-compose.yaml  grafana_data  install-docker.sh  Panel  pg_configs  prometheus
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# cat docker-compose.yaml
services:
  fastapi:
    build:
      context: ./Panel
      dockerfile: Dockerfile
    container_name: fastapi_app
    ports:
      - "8082:8000"
    depends_on:
      - postgres
      - kafka
    networks:
      - my-network
    environment:
      - DATABASE_URL=postgresql://postgres:Root_2118908@postgres:5432/Monitoring
      - KAFKA_BOOTSTRAP_SERVERS=kafka:9092

  kafka:
    image: bitnami/kafka:3.7
    container_name: kafka
    ports:
      - "9092:9092"
    networks:
      - my-network
    environment:
      - KAFKA_CFG_NODE_ID=1
      - KAFKA_CFG_PROCESS_ROLES=broker,controller
      - KAFKA_CFG_CONTROLLER_QUORUM_VOTERS=1@kafka:9093
      - KAFKA_CFG_LISTENERS=PLAINTEXT://:9092,CONTROLLER://:9093
      - KAFKA_CFG_ADVERTISED_LISTENERS=PLAINTEXT://172.17.214.38:9092
      - KAFKA_CFG_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT
      - KAFKA_CFG_CONTROLLER_LISTENER_NAMES=CONTROLLER
      - KAFKA_CFG_AUTO_CREATE_TOPICS_ENABLE=true
      - KAFKA_CFG_LOG_RETENTION_HOURS=72
    volumes:
      - /home/sisco/HSE_Monitoring/database_data/kafka:/bitnami/kafka

  kafka-ui:
    container_name: kafka-ui
    image: provectuslabs/kafka-ui:v0.7.2
    ports:
      - "9090:8080"
    depends_on:
      - kafka
    networks:
      - my-network
    environment:
      KAFKA_CLUSTERS_0_NAME: local
      KAFKA_CLUSTERS_0_BOOTSTRAPSERVERS: kafka:9092
      DYNAMIC_CONFIG_ENABLED: "true"

  clickhouse:
    image: clickhouse/clickhouse-server:25.2
    container_name: clickhouse
    ports:
      - "8123:8123"
      - "9000:9000"
    networks:
      - my-network
    environment:
      - CLICKHOUSE_USER=default
      - CLICKHOUSE_PASSWORD=Root_2118908
      - TZ=Asia/Tehran
    volumes:
      - /home/sisco/HSE_Monitoring/database_data/clickhouse:/var/lib/clickhouse

  postgres:
    image: debezium/postgres:16
    container_name: postgres
    networks:
      - my-network
    environment:
      POSTGRES_DB: Monitoring
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: Root_2118908
    ports:
      - "6543:5432"
    volumes:
      - /home/sisco/HSE_Monitoring/assets:/assets
      - /home/sisco/HSE_Monitoring/database_data/postgres:/var/lib/postgresql/data
      - /home/sisco/HSE_Monitoring/pg_configs:/etc/postgresql/custom_configs

  pgadmin:
    image: dpage/pgadmin4:latest
    container_name: pgadmin
    ports:
      - "5050:80"
    networks:
      - my-network
    environment:
      PGADMIN_DEFAULT_EMAIL: sinamoghadary@gmail.com
      PGADMIN_DEFAULT_PASSWORD: Sina_2118908
    depends_on:
      - postgres

  prometheus:
    image: prom/prometheus:v3.5.0
    container_name: prometheus
    restart: unless-stopped
    extra_hosts:
      - "host.docker.internal:host-gateway"
    ports:
      - "9093:9090"
    volumes:
      - ./prometheus/prometheus.yml:/etc/prometheus/prometheus.yml:ro
      - prometheus_data:/prometheus
    command:
      - "--config.file=/etc/prometheus/prometheus.yml"
      - "--storage.tsdb.retention.time=30d"
      - "--web.enable-lifecycle"
    networks:
      - my-network

networks:
  my-network:
    driver: bridge

volumes:
  prometheus_data:
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring#
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# docker ps
CONTAINER ID   IMAGE                               COMMAND                  CREATED        STATUS                 PORTS                                                                                                NAMES
19a587a7d874   prom/prometheus:v3.5.0              "/bin/prometheus --c…"   2 weeks ago    Up 2 weeks             0.0.0.0:9093->9090/tcp, [::]:9093->9090/tcp                                                          prometheus
d4197caaab14   hse_monitoring-fastapi              "uvicorn main:app --…"   2 weeks ago    Up 2 weeks             0.0.0.0:8082->8000/tcp, [::]:8082->8000/tcp                                                          fastapi_app
25b390fe0bd3   dpage/pgadmin4:latest               "/entrypoint.sh"         2 weeks ago    Up 2 weeks             443/tcp, 0.0.0.0:5050->80/tcp, [::]:5050->80/tcp                                                     pgadmin
6951ac2ce45f   provectuslabs/kafka-ui:v0.7.2       "/bin/sh -c 'java --…"   2 weeks ago    Up 2 weeks             0.0.0.0:9090->8080/tcp, [::]:9090->8080/tcp                                                          kafka-ui
4d151e0bc863   debezium/postgres:16                "docker-entrypoint.s…"   2 weeks ago    Up 2 weeks             0.0.0.0:6543->5432/tcp, [::]:6543->5432/tcp                                                          postgres
93a0cef01c49   clickhouse/clickhouse-server:25.2   "/entrypoint.sh"         2 weeks ago    Up 2 weeks             0.0.0.0:8123->8123/tcp, [::]:8123->8123/tcp, 0.0.0.0:9000->9000/tcp, [::]:9000->9000/tcp, 9009/tcp   clickhouse
ec37033dd031   gcr.io/cadvisor/cadvisor:v0.47.0    "/usr/bin/cadvisor -…"   2 weeks ago    Up 2 weeks (healthy)   0.0.0.0:8088->8080/tcp, [::]:8088->8080/tcp                                                          cadvisor
64ecd7b36a53   bitnami/kafka:3.7                   "/opt/bitnami/script…"   2 weeks ago    Up 2 weeks             0.0.0.0:9092->9092/tcp, [::]:9092->9092/tcp                                                          kafka
25240b275b6b   portainer/portainer-ce:latest       "/portainer"             3 months ago   Up 2 weeks             0.0.0.0:8000->8000/tcp, [::]:8000->8000/tcp, 0.0.0.0:9443->9443/tcp, [::]:9443->9443/tcp, 9000/tcp   portainer
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring#
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# cd Panel/
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# LS
ls
^Croot@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel#
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# ls
Dockerfile  main.py  requirements.txt
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# nano main.py
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# nano main.py
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# cd ..
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# ls
assets  database_data  docker-compose.yaml  grafana_data  install-docker.sh  Panel  pg_configs  prometheus
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# cat docker-compose.yaml
services:
  fastapi:
    build:
      context: ./Panel
      dockerfile: Dockerfile
    container_name: fastapi_app
    ports:
      - "8082:8000"
    depends_on:
      - postgres
      - kafka
    networks:
      - my-network
    environment:
      - DATABASE_URL=postgresql://postgres:Root_2118908@postgres:5432/Monitoring
      - KAFKA_BOOTSTRAP_SERVERS=kafka:9092

  kafka:
    image: bitnami/kafka:3.7
    container_name: kafka
    ports:
      - "9092:9092"
    networks:
      - my-network
    environment:
      - KAFKA_CFG_NODE_ID=1
      - KAFKA_CFG_PROCESS_ROLES=broker,controller
      - KAFKA_CFG_CONTROLLER_QUORUM_VOTERS=1@kafka:9093
      - KAFKA_CFG_LISTENERS=PLAINTEXT://:9092,CONTROLLER://:9093
      - KAFKA_CFG_ADVERTISED_LISTENERS=PLAINTEXT://172.17.214.38:9092
      - KAFKA_CFG_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT
      - KAFKA_CFG_CONTROLLER_LISTENER_NAMES=CONTROLLER
      - KAFKA_CFG_AUTO_CREATE_TOPICS_ENABLE=true
      - KAFKA_CFG_LOG_RETENTION_HOURS=72
    volumes:
      - /home/sisco/HSE_Monitoring/database_data/kafka:/bitnami/kafka

  kafka-ui:
    container_name: kafka-ui
    image: provectuslabs/kafka-ui:v0.7.2
    ports:
      - "9090:8080"
    depends_on:
      - kafka
    networks:
      - my-network
    environment:
      KAFKA_CLUSTERS_0_NAME: local
      KAFKA_CLUSTERS_0_BOOTSTRAPSERVERS: kafka:9092
      DYNAMIC_CONFIG_ENABLED: "true"

  clickhouse:
    image: clickhouse/clickhouse-server:25.2
    container_name: clickhouse
    ports:
      - "8123:8123"
      - "9000:9000"
    networks:
      - my-network
    environment:
      - CLICKHOUSE_USER=default
      - CLICKHOUSE_PASSWORD=Root_2118908
      - TZ=Asia/Tehran
    volumes:
      - /home/sisco/HSE_Monitoring/database_data/clickhouse:/var/lib/clickhouse

  postgres:
    image: debezium/postgres:16
    container_name: postgres
    networks:
      - my-network
    environment:
      POSTGRES_DB: Monitoring
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: Root_2118908
    ports:
      - "6543:5432"
    volumes:
      - /home/sisco/HSE_Monitoring/assets:/assets
      - /home/sisco/HSE_Monitoring/database_data/postgres:/var/lib/postgresql/data
      - /home/sisco/HSE_Monitoring/pg_configs:/etc/postgresql/custom_configs

  pgadmin:
    image: dpage/pgadmin4:latest
    container_name: pgadmin
    ports:
      - "5050:80"
    networks:
      - my-network
    environment:
      PGADMIN_DEFAULT_EMAIL: sinamoghadary@gmail.com
      PGADMIN_DEFAULT_PASSWORD: Sina_2118908
    depends_on:
      - postgres

  prometheus:
    image: prom/prometheus:v3.5.0
    container_name: prometheus
    restart: unless-stopped
    extra_hosts:
      - "host.docker.internal:host-gateway"
    ports:
      - "9093:9090"
    volumes:
      - ./prometheus/prometheus.yml:/etc/prometheus/prometheus.yml:ro
      - prometheus_data:/prometheus
    command:
      - "--config.file=/etc/prometheus/prometheus.yml"
      - "--storage.tsdb.retention.time=30d"
      - "--web.enable-lifecycle"
    networks:
      - my-network

networks:
  my-network:
    driver: bridge

volumes:
  prometheus_data:
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# nano docker-compose.yaml
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# docker compose restart fastapi
[+] Restarting 1/1
 ✔ Container fastapi_app  Started                                                                                                                           0.8s
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# docker compose up -d --build --no-deps fastapi
[+] Building 436.5s (8/9)                                                                                                                         docker:default
 => [fastapi internal] load build definition from Dockerfile                                                                                                0.3s
 => => transferring dockerfile: 652B                                                                                                                        0.1s
 => [fastapi internal] load metadata for docker.io/library/python:3.12-slim                                                                                 1.2s
 => [fastapi internal] load .dockerignore                                                                                                                   0.0s
 => => transferring context: 2B                                                                                                                             0.0s
 => [fastapi 1/5] FROM docker.io/library/python:3.12-slim@sha256:2c941e860699f878900b0edc2403613c234d4b32eda3cc9fa7036991a2a63c4a                           4.5s
 => => resolve docker.io/library/python:3.12-slim@sha256:2c941e860699f878900b0edc2403613c234d4b32eda3cc9fa7036991a2a63c4a                                   0.0s
 => => sha256:b952fe9f6810de5dac5d24a3983aee5fce3884f7359a533ba325e48ecb745994 12.12MB / 12.12MB                                                            3.1s
 => => sha256:6760bfe2ff00c4530bc73b2f88a1e9615a56c9a77028f41f8bb4b978d08b8439 248B / 248B                                                                  0.3s
 => => sha256:fa18dfb1257a9c1afc75e233c55b0195dd02b8d6d18dd7e24c10238b039e7742 1.29MB / 1.29MB                                                              1.1s
 => => extracting sha256:fa18dfb1257a9c1afc75e233c55b0195dd02b8d6d18dd7e24c10238b039e7742                                                                   0.7s
 => => extracting sha256:b952fe9f6810de5dac5d24a3983aee5fce3884f7359a533ba325e48ecb745994                                                                   1.0s
 => => extracting sha256:6760bfe2ff00c4530bc73b2f88a1e9615a56c9a77028f41f8bb4b978d08b8439                                                                   0.1s
 => [fastapi internal] load build context                                                                                                                   0.1s
 => => transferring context: 7.97kB                                                                                                                         0.0s
 => [fastapi 2/5] WORKDIR /app                                                                                                                              0.3s
 => [fastapi 3/5] COPY requirements.txt .                                                                                                                   0.1s
 => CANCELED [fastapi 4/5] RUN pip install --no-cache-dir -r requirements.txt                                                                             429.2s
canceled
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# docker compose up -d --build --no-deps fastapi
[+] Building 1105.7s (11/11) FINISHED                                                                                                             docker:default
 => [fastapi internal] load build definition from Dockerfile                                                                                                0.0s
 => => transferring dockerfile: 665B                                                                                                                        0.0s
 => [fastapi internal] load metadata for docker.io/library/python:3.12-slim                                                                                 0.5s
 => [fastapi internal] load .dockerignore                                                                                                                   0.0s
 => => transferring context: 2B                                                                                                                             0.0s
 => [fastapi 1/5] FROM docker.io/library/python:3.12-slim@sha256:2c941e860699f878900b0edc2403613c234d4b32eda3cc9fa7036991a2a63c4a                           0.0s
 => => resolve docker.io/library/python:3.12-slim@sha256:2c941e860699f878900b0edc2403613c234d4b32eda3cc9fa7036991a2a63c4a                                   0.0s
 => [fastapi internal] load build context                                                                                                                   0.0s
 => => transferring context: 756B                                                                                                                           0.0s
 => CACHED [fastapi 2/5] WORKDIR /app                                                                                                                       0.0s
 => CACHED [fastapi 3/5] COPY requirements.txt .                                                                                                            0.0s
 => [fastapi 4/5] RUN pip install --no-cache-dir -r requirements.txt                                                                                     1017.6s
 => [fastapi 5/5] COPY . .                                                                                                                                  1.9s
 => [fastapi] exporting to image                                                                                                                           85.1s
 => => exporting layers                                                                                                                                    64.5s
 => => exporting manifest sha256:96eb7b911c4cdc8b59076658ce8565cbc2e65c0d9c80c9efafcb2b422cb41dc8                                                           0.0s
 => => exporting config sha256:f679cc0c7e9ad11236ec7e04c55346bc64cc7dcee0d678762bd23c820e55226e                                                             0.0s
 => => exporting attestation manifest sha256:bfa1cd669d99762889514e984152eb14bf95a7fa2d13308c77714e7aa086da9a                                               0.0s
 => => exporting manifest list sha256:495498c5a8dc4cc669afdcd736955102d2412225ce1e2006a7933b3f5a31a74b                                                      0.0s
 => => naming to docker.io/library/hse_monitoring-fastapi:latest                                                                                            0.2s
 => => unpacking to docker.io/library/hse_monitoring-fastapi:latest                                                                                        20.1s
 => [fastapi] resolving provenance for metadata file                                                                                                        0.1s
WARN[1106] Found orphan containers ([cadvisor]) for this project. If you removed or renamed this service in your compose file, you can run this command with the --remove-orphans flag to clean it up.
[+] Running 1/1
 ✔ Container fastapi_app  Started                                                                                                                          14.2s
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring#
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# docker ps
CONTAINER ID   IMAGE                               COMMAND                  CREATED          STATUS                 PORTS                                                                                                NAMES
f766b8a4b203   hse_monitoring-fastapi              "uvicorn main:app --…"   39 seconds ago   Up 26 seconds          0.0.0.0:8082->8000/tcp, [::]:8082->8000/tcp                                                          fastapi_app
19a587a7d874   prom/prometheus:v3.5.0              "/bin/prometheus --c…"   2 weeks ago      Up 2 weeks             0.0.0.0:9093->9090/tcp, [::]:9093->9090/tcp                                                          prometheus
25b390fe0bd3   dpage/pgadmin4:latest               "/entrypoint.sh"         2 weeks ago      Up 2 weeks             443/tcp, 0.0.0.0:5050->80/tcp, [::]:5050->80/tcp                                                     pgadmin
6951ac2ce45f   provectuslabs/kafka-ui:v0.7.2       "/bin/sh -c 'java --…"   2 weeks ago      Up 2 weeks             0.0.0.0:9090->8080/tcp, [::]:9090->8080/tcp                                                          kafka-ui
4d151e0bc863   debezium/postgres:16                "docker-entrypoint.s…"   2 weeks ago      Up 2 weeks             0.0.0.0:6543->5432/tcp, [::]:6543->5432/tcp                                                          postgres
93a0cef01c49   clickhouse/clickhouse-server:25.2   "/entrypoint.sh"         2 weeks ago      Up 2 weeks             0.0.0.0:8123->8123/tcp, [::]:8123->8123/tcp, 0.0.0.0:9000->9000/tcp, [::]:9000->9000/tcp, 9009/tcp   clickhouse
ec37033dd031   gcr.io/cadvisor/cadvisor:v0.47.0    "/usr/bin/cadvisor -…"   2 weeks ago      Up 2 weeks (healthy)   0.0.0.0:8088->8080/tcp, [::]:8088->8080/tcp                                                          cadvisor
64ecd7b36a53   bitnami/kafka:3.7                   "/opt/bitnami/script…"   2 weeks ago      Up 2 weeks             0.0.0.0:9092->9092/tcp, [::]:9092->9092/tcp                                                          kafka
25240b275b6b   portainer/portainer-ce:latest       "/portainer"             3 months ago     Up 2 weeks             0.0.0.0:8000->8000/tcp, [::]:8000->8000/tcp, 0.0.0.0:9443->9443/tcp, [::]:9443->9443/tcp, 9000/tcp   portainer
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# docker compose up -d --build --no-deps fastapiclient_loop: send disconnect: Broken pipe
(base) sinamoghadary@SHQ-KE-ICT-AO06:~$ ssh sisco@172.17.214.38
sisco@172.17.214.38's password:
Welcome to Ubuntu 22.04.3 LTS (GNU/Linux 5.15.0-177-generic x86_64)

 * Documentation:  https://help.ubuntu.com
 * Management:     https://landscape.canonical.com
 * Support:        https://ubuntu.com/advantage

  System information as of Tue Aug 25 11:53:01 AM +0330 2026

  System load:                      0.46728515625
  Usage of /:                       18.0% of 243.92GB
  Memory usage:                     44%
  Swap usage:                       12%
  Processes:                        301
  Users logged in:                  1
  IPv4 address for br-0e0c9b898729: 172.19.0.1
  IPv4 address for br-559ebe9235a2: 172.20.0.1
  IPv4 address for br-da271825ae00: 172.21.0.1
  IPv4 address for docker0:         172.18.0.1
  IPv4 address for ens160:          172.17.214.38

 * Strictly confined Kubernetes makes edge and IoT secure. Learn how MicroK8s
   just raised the bar for easy, resilient and secure K8s cluster deployment.

   https://ubuntu.com/engage/secure-kubernetes-at-the-edge

Expanded Security Maintenance for Applications is not enabled.

102 updates can be applied immediately.
6 of these updates are standard security updates.
To see these additional updates run: apt list --upgradable

2 additional security updates can be applied with ESM Apps.
Learn more about enabling ESM Apps service at https://ubuntu.com/esm

New release '24.04.4 LTS' available.
Run 'do-release-upgrade' to upgrade to it.


1 updates could not be installed automatically. For more details,
see /var/log/unattended-upgrades/unattended-upgrades.log

*** System restart required ***
Last login: Mon Aug 24 14:42:53 2026 from 172.17.208.52
sisco@shq-ke-vl-mo28:~$ su
Password:
root@shq-ke-vl-mo28:/home/sisco# cd HSE_Monitoring/
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# ls
assets  database_data  docker-compose.yaml  grafana_data  install-docker.sh  Panel  pg_configs  prometheus
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring# cd Panel/
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# LS
^LLS: command not found
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel#
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# ls
Dockerfile  main.py  __pycache__  requirements.txt
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# nano main.py
root@shq-ke-vl-mo28:/home/sisco/HSE_Monitoring/Panel# cat main.py
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
CLICKHOUSE_HOST = os.getenv('CLICKHOUSE_HOST', '172.17.214.38')
CLICKHOUSE_PORT = int(os.getenv('CLICKHOUSE_PORT', 8123))
CLICKHOUSE_USER = os.getenv('CLICKHOUSE_USER', 'default')
CLICKHOUSE_PASSWORD = os.getenv('CLICKHOUSE_PASSWORD', 'Root_2118908')
DATABASE = os.getenv('CLICKHOUSE_DB', 'Monitoring')

# --- تنظیمات PostgreSQL ---
PG_HOST = os.getenv('PG_HOST', '172.17.214.38')
PG_PORT = int(os.getenv('PG_PORT', 6543))
PG_USER = os.getenv('PG_USER', 'postgres')
PG_PASSWORD = os.getenv('PG_PASSWORD', 'Root_2118908')
PG_DATABASE = os.getenv('PG_DATABASE', 'Monitoring')


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
        query = f"SELECT * FROM UserActivities ORDER BY Timestamp DESC LIMIT {limit}"
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