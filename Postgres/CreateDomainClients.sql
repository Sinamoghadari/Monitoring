-- ==========================================
-- جدول برای ذخیره لیست کلاینت‌های دومین Active Directory
-- ==========================================

CREATE TABLE IF NOT EXISTS domain_clients (
    id SERIAL PRIMARY KEY,
    computer_name VARCHAR(255) NOT NULL UNIQUE,
    windows_username VARCHAR(255),
    last_logon_date TIMESTAMP,
    guid VARCHAR(100),
    collected_at TIMESTAMP,
    synced_at TIMESTAMP DEFAULT NOW(),
    
    -- ایندکس‌ها برای بهتری عملکرد جستجو
    CONSTRAINT uk_computer_name UNIQUE(computer_name)
);

-- ایجاد ایندکس برای جستجوهای بهتر
CREATE INDEX IF NOT EXISTS idx_domain_clients_synced_at 
    ON domain_clients(synced_at DESC);

CREATE INDEX IF NOT EXISTS idx_domain_clients_computer_name 
    ON domain_clients(computer_name);

-- توضیح جدول
COMMENT ON TABLE domain_clients IS 'لیست کامپیوتر‌های دومین Active Directory که به‌طور دوره‌ای آپدیت می‌شود';
COMMENT ON COLUMN domain_clients.id IS 'شناسه یکتا';
COMMENT ON COLUMN domain_clients.computer_name IS 'نام کامپیوتر';
COMMENT ON COLUMN domain_clients.windows_username IS 'نام کاربری ویندوز / اکانت کلاینت';
COMMENT ON COLUMN domain_clients.last_logon_date IS 'آخرین زمان ورود به سیستم';
COMMENT ON COLUMN domain_clients.guid IS 'شناسه یکتای جهانی Active Directory';
COMMENT ON COLUMN domain_clients.collected_at IS 'زمان جمع‌آوری اطلاعات';
COMMENT ON COLUMN domain_clients.synced_at IS 'زمان ذخیره در دیتابیس';
