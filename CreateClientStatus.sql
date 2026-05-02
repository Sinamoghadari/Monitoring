CREATE TABLE IF NOT EXISTS client_status (
    windows_sid VARCHAR(255) PRIMARY KEY,
    computer_name VARCHAR(255),
    windows_username VARCHAR(255),
    last_heartbeat TIMESTAMP NOT NULL,
    status VARCHAR(50) DEFAULT 'Online'
);
