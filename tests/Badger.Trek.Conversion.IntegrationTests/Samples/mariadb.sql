CREATE SEQUENCE ticket_no START WITH 1000 INCREMENT BY 1;

CREATE TABLE customers (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    name VARCHAR(100) NOT NULL,
    email VARCHAR(320) NULL,
    tier ENUM('free', 'pro', 'enterprise') NOT NULL DEFAULT 'free',
    active TINYINT(1) NOT NULL DEFAULT 1,
    ref UUID NOT NULL DEFAULT uuid(),
    settings JSON NULL,
    notes TEXT NULL,
    joined DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    PRIMARY KEY (id),
    UNIQUE KEY customers_email (email)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_uca1400_ai_ci COMMENT='people who buy things';

CREATE TABLE orders (
    id BIGINT NOT NULL AUTO_INCREMENT,
    customer_id INT UNSIGNED NOT NULL,
    ticket BIGINT NOT NULL DEFAULT NEXTVAL(ticket_no),
    total DECIMAL(10,2) NOT NULL DEFAULT 0.00,
    total_with_tax DECIMAL(12,2) AS (total * 1.2) PERSISTENT,
    status VARCHAR(20) NOT NULL DEFAULT 'open',
    placed_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (id),
    KEY orders_customer (customer_id, placed_at),
    CONSTRAINT orders_customer_fk FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE CASCADE,
    CONSTRAINT orders_total_positive CHECK (total >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_uca1400_ai_ci;

CREATE VIEW customer_totals AS
SELECT c.id, c.name, COUNT(o.id) AS orders, IFNULL(SUM(o.total), 0) AS spent, JSON_VALUE(c.settings, '$.theme') AS theme
FROM customers c LEFT JOIN orders o ON o.customer_id = c.id
GROUP BY c.id, c.name, c.settings;

INSERT INTO customers (name, email, tier, active, ref, settings, notes) VALUES
    ('Ada Lovelace', 'ada@example.com', 'pro', 1, '6f9619ff-8b86-d011-b42d-00c04fc964ff', '{"theme": "dark"}', 'tab\tand\nnewline \\ backslash'),
    ('Grace Hopper', NULL, 'free', 0, uuid(), NULL, NULL),
    ('Chloé "Tab"', 'chloe@example.com', 'enterprise', 1, uuid(), '[1, 2]', 'unicode ✓');

INSERT INTO orders (customer_id, total, status) VALUES
    (1, 29.97, 'open'),
    (1, 10.00, 'shipped'),
    (3, 5.50, 'open');
