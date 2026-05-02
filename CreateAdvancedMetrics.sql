CREATE TABLE advanced_system_metrics (
    id SERIAL PRIMARY KEY,
    windows_sid VARCHAR(255),
    windows_username VARCHAR(255),
    computer_name VARCHAR(255) ,
    motherboard_serial VARCHAR(255), 
    cpu_usage_percent REAL,
    logical_cores INTEGER,
    physical_cores INTEGER,
    cpu_temperature REAL,
    total_ram_mb REAL,
    used_ram_mb REAL,
    free_ram_mb REAL,
    system_uptime_seconds BIGINT,
    active_processes INTEGER,
    active_threads INTEGER,
    open_handles INTEGER,
    boot_time TIMESTAMP,
    failed_login_attempts INTEGER,
    antivirus_status VARCHAR(255),
    firewall_status VARCHAR(255),
    usb_devices_count INTEGER,
    storage_details JSONB,
    network_details JSONB,
    network_trace JSONB,  
    disk_models JSONB,      -- ا مدل و برند هارد دیسک‌ها
    top_processes JSONB,    --  برنامه پرمصرف سیستم
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
