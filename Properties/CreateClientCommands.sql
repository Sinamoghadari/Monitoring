CREATE TABLE IF NOT EXISTS client_commands (
    id SERIAL PRIMARY KEY,
    computer_name VARCHAR(255) NULL,       
    windows_username VARCHAR(255) NULL,    
    command VARCHAR(1000) NOT NULL,  
    status VARCHAR(20) DEFAULT 'pending',
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);