CREATE TABLE IF NOT EXISTS user_activity (
    id SERIAL PRIMARY KEY,
    windows_sid VARCHAR(255) NOT NULL,
    windows_username VARCHAR(255),
    computer_name VARCHAR(255) ,
    session_date DATE NOT NULL,
    session_start_time TIME,
    session_end_time TIME,
    keyboard_activity_seconds REAL DEFAULT 0,
    mouse_activity_seconds REAL DEFAULT 0,
    total_activity_seconds REAL DEFAULT 0,
    primary_alarm_count INT DEFAULT 0,
    primary_alarm_close_count INT DEFAULT 0,
    secondary_alarm_count INT DEFAULT 0,
    session_close_counter INT DEFAULT 0,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT unique_user_session UNIQUE (windows_sid, session_date)
);
