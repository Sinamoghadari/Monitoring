DO $$ 
BEGIN
    -- اصلاح نام جستجو شده در pg_type
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'client_commands_v2_enum') THEN
        CREATE TYPE client_commands_v2_enum AS ENUM (
            'executed',
            'pending'
        );
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS client_commands (
	id SERIAL PRIMARY KEY ,
	computer_name Varchar(255) ,
	status client_commands_v2_enum ,
	windows_username Varchar(255) ,
	command JSONB
);