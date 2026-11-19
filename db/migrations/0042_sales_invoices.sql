-- 0042 sales invoices (11, 6.4, 6.9): the link records of an order-built sales document. The journal document (AR_INVOICE, origin ORDER) is posted by the finance pipeline in the same
-- transaction and binds the link records by its detail_digest (ADR-052); the link records are not chained themselves. Insert-only: a reversal inserts mirror rows. The command runs as the
-- posting role, which gets the two tables and the one updatable column that locks an order row. Not here: credit notes, purchase invoices, the IC columns beyond the id.

create table if not exists core.sales_invoice (
    tenant_id              uuid not null references platform.tenant (tenant_id),
    company_id             uuid not null,
    sales_invoice_id       uuid not null default gen_random_uuid(),
    invoice_kind           core.code not null check (invoice_kind in ('INVOICE', 'CREDIT_NOTE', 'REVERSAL')),
    customer_partner_id    uuid not null,
    invoice_date           date not null,
    due_date               date,
    payment_terms_id       uuid,
    order_currency         char(3) not null references platform.ref_currency (currency_code),
    journal_document_id    uuid not null,
    credits_invoice_id     uuid,
    credit_kind            core.code check (credit_kind in ('RETURN', 'AMOUNT')),
    reversal_of_sales_invoice_id uuid,
    reason_code            core.code,
    invoice_mode           core.code check (invoice_mode in ('CORE_BUILDS', 'APP_SUPPLIES')),
    ic_transaction_id      uuid,
    source_system          core.code not null,
    source_document_ref    varchar(100),
    created_at             timestamptz not null default now(),
    created_by             varchar(100) not null,
    correlation_id         varchar(100) not null,
    idempotency_key        varchar(100) not null,
    primary key (tenant_id, company_id, sales_invoice_id),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, journal_document_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, customer_partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, company_id, journal_document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, credits_invoice_id) references core.sales_invoice (tenant_id, company_id, sales_invoice_id),
    foreign key (tenant_id, company_id, reversal_of_sales_invoice_id) references core.sales_invoice (tenant_id, company_id, sales_invoice_id),
    check ((invoice_kind = 'CREDIT_NOTE') = (credits_invoice_id is not null)),
    check ((invoice_kind = 'REVERSAL') = (reversal_of_sales_invoice_id is not null))
);

create table if not exists core.sales_invoice_line (
    tenant_id              uuid not null,
    company_id             uuid not null,
    sales_invoice_id       uuid not null,
    line_no                int not null check (line_no >= 1),
    sales_order_id         uuid not null,
    sales_order_line_no    int not null,
    delivery_id            uuid,
    delivery_line_no       int,
    dropship_confirmation_id uuid,
    quantity               numeric(19,6) not null,
    unit_code              varchar(3) not null references platform.ref_unit (unit_code),
    base_quantity          numeric(19,6) not null,
    unit_price             core.amt not null,
    discount_pct           core.rate not null,
    net_amount             core.amt not null,
    order_unit_price       core.amt,
    order_discount_pct     core.rate,
    tax_code_id            uuid,
    tax_code_version_id    uuid,
    tax_date               date,
    journal_line_no        int not null,
    primary key (tenant_id, company_id, sales_invoice_id, line_no),
    foreign key (tenant_id, company_id, sales_invoice_id) references core.sales_invoice (tenant_id, company_id, sales_invoice_id),
    foreign key (tenant_id, company_id, sales_order_id, sales_order_line_no) references core.sales_order_line (tenant_id, company_id, sales_order_id, line_no),
    foreign key (tenant_id, company_id, delivery_id, delivery_line_no) references core.sales_delivery_line (tenant_id, company_id, delivery_id, line_no),
    check ((delivery_id is null) = (delivery_line_no is null))
);
create index if not exists sales_invoice_line_order on core.sales_invoice_line (tenant_id, company_id, sales_order_id, sales_order_line_no);
create index if not exists sales_invoice_line_delivery on core.sales_invoice_line (tenant_id, company_id, delivery_id, delivery_line_no) where delivery_id is not null;

do $$
declare t text;
begin
    foreach t in array array['sales_invoice', 'sales_invoice_line'] loop
        execute format('alter table core.%I enable row level security', t);
        execute format('alter table core.%I force row level security', t);
        if not exists (select 1 from pg_policies where schemaname = 'core' and tablename = t and policyname = 'tenant_isolation') then
            execute format('create policy tenant_isolation on core.%I using (tenant_id = core.current_tenant_id()) with check (tenant_id = core.current_tenant_id())', t);
        end if;
        execute format('revoke all on core.%I from public', t);
        execute format('grant select on core.%I to cmd_read, cmd_config, cmd_period, cmd_post, cmd_clearing, cmd_retention, cmd_stock, cmd_order', t);
        if exists (select 1 from pg_roles where rolname = 'cmd_closing') then execute format('grant select on core.%I to cmd_closing', t); end if;
        if exists (select 1 from pg_roles where rolname = 'cmd_migration') then execute format('grant select on core.%I to cmd_migration', t); end if;
        execute format('grant insert on core.%I to cmd_post', t);
        execute format('drop trigger if exists %I on core.%I', t || '_append_only', t);
        execute format('create trigger %I before update on core.%I for each row execute function core.reject_change()', t || '_append_only', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_delete', t);
        execute format('create trigger %I before delete on core.%I for each row execute function core.reject_delete()', t || '_no_delete', t);
        execute format('drop trigger if exists %I on core.%I', t || '_no_truncate', t);
        execute format('create trigger %I before truncate on core.%I for each statement execute function core.reject_delete()', t || '_no_truncate', t);
    end loop;
end $$;

-- the posting role locks the order rows of an invoice command through one updatable column (11, 9), like the stock role
grant update (row_version) on core.sales_order, core.purchase_order to cmd_post;

insert into platform.schema_migration (version, description)
values ('0042', 'sales invoices: the link records of an order-built AR document')
on conflict (version) do nothing;
