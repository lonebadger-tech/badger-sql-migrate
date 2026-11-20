CREATE TABLE customers (
    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
    name VARCHAR(100) NOT NULL,
    email VARCHAR(320) NULL,
    tier ENUM('free', 'pro', 'enterprise') NOT NULL DEFAULT 'free',
    tags SET('vip', 'early', 'beta') NULL,
    active TINYINT(1) NOT NULL DEFAULT 1,
    settings JSON NULL,
    notes TEXT NULL,
    joined DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    PRIMARY KEY (id),
    UNIQUE KEY customers_email (email)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='people who buy things';

CREATE TABLE orders (
    id BIGINT NOT NULL AUTO_INCREMENT,
    customer_id INT UNSIGNED NOT NULL,
    total DECIMAL(10,2) NOT NULL DEFAULT 0.00,
    quantity SMALLINT NOT NULL DEFAULT 1 COMMENT 'items in the order',
    total_with_tax DECIMAL(12,2) GENERATED ALWAYS AS (total * 1.2) STORED,
    status VARCHAR(20) NOT NULL DEFAULT 'open',
    placed_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    shipped_on DATE NULL,
    receipt BLOB NULL,
    PRIMARY KEY (id),
    KEY orders_customer (customer_id, placed_at),
    CONSTRAINT orders_customer_fk FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE CASCADE,
    CONSTRAINT orders_total_positive CHECK (total >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE order_items (
    order_id BIGINT NOT NULL,
    line SMALLINT NOT NULL,
    product VARCHAR(200) NOT NULL,
    price DECIMAL(8,2) NOT NULL,
    PRIMARY KEY (order_id, line),
    CONSTRAINT order_items_order_fk FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE VIEW customer_totals AS
SELECT c.id, c.name, COUNT(o.id) AS orders, COALESCE(SUM(o.total), 0) AS spent
FROM customers c LEFT JOIN orders o ON o.customer_id = c.id
GROUP BY c.id, c.name;

INSERT INTO customers (name, email, tier, tags, active, settings, notes) VALUES
    ('Ada Lovelace', 'ada@example.com', 'pro', 'vip,early', 1, '{"theme": "dark"}', 'tab\tand\nnewline \\ backslash'),
    ('Grace Hopper', NULL, 'free', NULL, 0, NULL, NULL),
    ('Chloé "Tab"', 'chloe@example.com', 'enterprise', 'beta', 1, '[1, 2]', 'unicode ✓');

INSERT INTO orders (customer_id, total, quantity, status, shipped_on, receipt) VALUES
    (1, 29.97, 3, 'open', NULL, X'00FF10'),
    (1, 10.00, 1, 'shipped', '2026-01-02', NULL),
    (3, 5.50, 2, 'open', NULL, NULL);

INSERT INTO order_items (order_id, line, product, price) VALUES
    (1, 1, 'Widget', 9.99), (1, 2, 'Gadget', 19.98), (2, 1, 'Sprocket', 10.00), (3, 1, 'Cog', 5.50);
