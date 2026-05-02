CREATE TABLE IF NOT EXISTS alarm_images(image_name text , image_data bytea) ;

INSERT INTO public.alarm_images (image_name, image_data)
VALUES 
('1.png', pg_read_binary_file('/assets/1.png')),
('2.png', pg_read_binary_file('/assets/2.png')),
('3.png', pg_read_binary_file('/assets/3.png')),
('4.png', pg_read_binary_file('/assets/4.png')),
('5.png', pg_read_binary_file('/assets/5.png')),
('6.png', pg_read_binary_file('/assets/6.png')),
('7.png', pg_read_binary_file('/assets/7.png')),
('8.png', pg_read_binary_file('/assets/8.png')),
('9.png', pg_read_binary_file('/assets/9.png')),
('10.png', pg_read_binary_file('/assets/10.png')),
('11.png', pg_read_binary_file('/assets/11.png'));
