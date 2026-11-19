CREATE SCHEMA app;
CREATE TYPE app.order_status AS ENUM ('new', 'paid', 'shipped');
CREATE DOMAIN app.positive_amount AS numeric(12,2) CHECK (VALUE >= 0);
CREATE TABLE app.customers (
    id serial PRIMARY KEY,
    name text NOT NULL,
    email varchar(320) UNIQUE,
    active boolean NOT NULL DEFAULT true,
    tags text[],
    profile jsonb,
    created timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE app.customers IS 'People who buy things';
COMMENT ON COLUMN app.customers.email IS 'Unique when present';
CREATE TABLE app.orders (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES app.customers (id) ON DELETE CASCADE,
    status app.order_status NOT NULL DEFAULT 'new',
    total app.positive_amount NOT NULL DEFAULT 0,
    total_with_tax numeric(13,2) GENERATED ALWAYS AS (total * 1.2) STORED,
    placed_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz,
    note text CHECK (char_length(note) <= 500)
);
CREATE INDEX orders_unpaid ON app.orders (customer_id) WHERE status = 'new';
CREATE INDEX customers_lower_name ON app.customers (lower(name));
CREATE TABLE app.order_log (id bigserial PRIMARY KEY, order_id bigint NOT NULL, event text NOT NULL, at timestamptz NOT NULL DEFAULT now());

CREATE VIEW app.customer_totals AS
SELECT c.id, c.name, count(o.id) AS orders, COALESCE(sum(o.total), 0) AS spent, max(o.placed_at) AS last_order
FROM app.customers c LEFT JOIN app.orders o ON o.customer_id = c.id
WHERE c.active
GROUP BY c.id, c.name;

CREATE FUNCTION app.display_name(p_name text, p_email text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
  SELECT p_name || COALESCE(' <' || p_email || '>', '')
$$;

CREATE FUNCTION app.orders_since(since timestamptz) RETURNS TABLE (id bigint, total numeric) LANGUAGE sql STABLE AS $$
  SELECT o.id, o.total FROM app.orders o WHERE o.placed_at >= since
$$;

CREATE FUNCTION app.order_count(p_customer integer) RETURNS integer LANGUAGE plpgsql STABLE AS $$
DECLARE
  n integer;
BEGIN
  SELECT count(*) INTO n FROM app.orders WHERE customer_id = p_customer;
  IF n IS NULL THEN
    RETURN 0;
  END IF;
  RETURN n;
END;
$$;

CREATE FUNCTION app.mark_paid(p_order bigint) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
  v_rows integer;
BEGIN
  UPDATE app.orders SET status = 'paid' WHERE id = p_order AND status = 'new';
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  IF v_rows = 0 THEN
    RAISE EXCEPTION 'order % is not open', p_order;
  END IF;
  INSERT INTO app.order_log (order_id, event) VALUES (p_order, 'paid');
  RETURN v_rows;
END;
$$;

CREATE PROCEDURE app.ship_all() LANGUAGE plpgsql AS $$
DECLARE
  r record;
BEGIN
  FOR r IN SELECT id FROM app.orders WHERE status = 'paid' LOOP
    UPDATE app.orders SET status = 'shipped' WHERE id = r.id;
    INSERT INTO app.order_log (order_id, event) VALUES (r.id, 'shipped');
  END LOOP;
END;
$$;

CREATE FUNCTION app.touch() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at := now();
  RETURN NEW;
END;
$$;
CREATE TRIGGER orders_touch BEFORE UPDATE ON app.orders FOR EACH ROW EXECUTE FUNCTION app.touch();

CREATE FUNCTION app.log_new_orders() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO app.order_log (order_id, event) SELECT n.id, 'created' FROM new_orders n;
  RETURN NULL;
END;
$$;
CREATE TRIGGER orders_created AFTER INSERT ON app.orders REFERENCING NEW TABLE AS new_orders FOR EACH STATEMENT EXECUTE FUNCTION app.log_new_orders();

INSERT INTO app.customers (name, email, tags, profile) VALUES
  ('Ada Lovelace', 'ada@example.com', ARRAY['vip','early'], '{"tier": 1}'),
  ('Bob Smith', NULL, NULL, NULL),
  (E'Chloé "Tab\there"', 'chloe@example.com', ARRAY['eu'], '{"tier": 2, "langs": ["fr"]}');
INSERT INTO app.orders (customer_id, total, note) VALUES (1, 29.97, 'first'), (3, 120.00, NULL), (1, 5.00, E'line1\nline2');
SELECT app.mark_paid(1);
