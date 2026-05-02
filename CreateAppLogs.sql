CREATE TABLE IF NOT EXISTS app_logs (
    id SERIAL PRIMARY KEY,
    timestamp TIMESTAMP WITHOUT TIME ZONE NOT NULL,
    log_level VARCHAR(50),
    message TEXT,
    windows_username VARCHAR(100),
    windows_sid VARCHAR(100),
    computer_name VARCHAR(100),
    created_at TIMESTAMP WITHOUT TIME ZONE DEFAULT CURRENT_TIMESTAMP
);
