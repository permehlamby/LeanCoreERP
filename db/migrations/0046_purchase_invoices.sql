-- 0046 purchase invoices (11, 6.9, 7.2): the link records of a supplier invoice that is matched against purchase order lines and receipts. The journal document (AP_INVOICE, origin ORDER) is posted
-- by the finance pipeline in the same transaction and binds the link records by its detail_digest (ADR-052); the link records are not chained themselves. Insert-only: a reversal or a credit note
-- inserts rows of its own. The command runs as the posting role. The match clears the GR/IR credit of the receipt against the GR/IR debit of the invoice in the same transaction (7.2, 03), so
-- the posting role gets what the stock role got in 0040: the clearing tables, the CLEARING head and the S columns of an open item. Not here: credit notes, reversal, charges, direct receipts.

create table if not exists core.purchase_invoice (
    tenant_id              uuid not null references platform.tenant (tenant_id),
    company_id             uuid not null,
    purchase_invoice_id    uuid not null default gen_random_uuid(),
    invoice_kind           core.code not null check (invoice_kind in ('INVOICE', 'CREDIT_NOTE', 'REVERSAL')),
    supplier_partner_id    uuid not null,
    supplier_invoice_number varchar(100),
    invoice_date           date not null,
    due_date               date,
    currency               char(3) not null references platform.ref_currency (currency_code),
    gross_amount           core.amt not null,
    journal_document_id    uuid not null,
    stock_document_id      uuid,
    credits_invoice_id     uuid,
    credit_kind            core.code check (credit_kind in ('RETURN', 'AMOUNT')),
    reversal_of_purchase_invoice_id uuid,
    reason_code            core.code,
    ic_transaction_id      uuid,
    source_system          core.code not null,
    source_document_ref    varchar(100),
    created_at             timestamptz not null default now(),
    created_by             varchar(100) not null,
    correlation_id         varchar(100) not null,
    idempotency_key        varchar(100) not null,
    primary key (tenant_id, company_id, purchase_invoice_id),
    unique (tenant_id, idempotency_key),
    unique (tenant_id, company_id, journal_document_id),
    foreign key (tenant_id, company_id) references core.company (tenant_id, company_id),
    foreign key (tenant_id, supplier_partner_id) references core.business_partner (tenant_id, partner_id),
    foreign key (tenant_id, company_id, journal_document_id) references core.journal_document (tenant_id, company_id, document_id),
    foreign key (tenant_id, company_id, credits_invoice_id) references core.purchase_invoice (tenant_id, company_id, purchase_invoice_id),
    foreign key (tenant_id, company_id, reversal_of_purchase_invoice_id) references core.purchase_invoice (tenant_id, company_id, purchase_invoice_id),
    check ((invoice_kind = 'CREDIT_NOTE') = (credits_invoice_id is not null)),
    check ((invoice_kind = 'REVERSAL') = (reversal_of_purchase_invoice_id is not null)),
    check ((invoice_kind = 'REVERSAL') = (supplier_invoice_number is null))
);
-- a supplier invoice number is used once per supplier and company (7.2, ORDER.DUPLICATE_SUPPLIER_INVOICE)
create unique index if not exists purchase_invoice_supplier_number on core.purchase_invoice (tenant_id, company_id, supplier_partner_id, supplier_invoice_number)
    where invoice_kind in ('INVOICE', 'CREDIT_NOTE');

create table if not exists core.purchase_invoice_line (
    tenant_id              uuid not null,
    company_id             uuid not null,
    purchase_invoice_id    uuid not null,
    line_no                int not null check (line_no >= 1),
    line_kind              core.code not null check (line_kind in ('MATCHED', 'CHARGE')),
    description            varchar(200),
    charge_treatment       core.code check (charge_treatment in ('EXPENSE', 'LANDED_COST')),
    purchase_order_id      uuid,
    purchase_order_line_no int,
    receipt_id             uuid,
    receipt_line_no        int,
    stock_document_id      uuid,
    stock_document_line_no int,
    base_quantity          numeric(19,6) not null,
    unit_price             core.amt not null,
    net_amount             core.amt not null,
    tax_amount             core.amt not null,
    order_unit_price       core.amt,
    match_mode_used        core.code check (match_mode_used in ('THREE_WAY', 'TWO_WAY')),
    tolerance_used         core.rate,
    price_difference_lc    core.amt not null default 0,
    on_hand_part_lc        core.amt not null default 0,
    consumed_part_lc       core.amt not null default 0,
    dropship_part_lc       core.amt not null default 0,
    unreceived_gr_ir_lc    core.amt not null default 0,
    gr_ir_clearing_id      uuid,
    tax_code_id            uuid,
    tax_code_version_id    uuid,
    tax_date               date,
    journal_line_no        int not null,
    credits_invoice_line_no int,
    return_id              uuid,
    return_line_no         int,
    primary key (tenant_id, company_id, purchase_invoice_id, line_no),
    foreign key (tenant_id, company_id, purchase_invoice_id) references core.purchase_invoice (tenant_id, company_id, purchase_invoice_id),
    foreign key (tenant_id, company_id, purchase_order_id, purchase_order_line_no) references core.purchase_order_line (tenant_id, company_id, purchase_order_id, line_no),
    foreign key (tenant_id, company_id, receipt_id, receipt_line_no) references core.purchase_receipt_line (tenant_id, company_id, receipt_id, line_no),
    check ((purchase_order_id is null) = (purchase_order_line_no is null)),
    check ((receipt_id is null) = (receipt_line_no is null)),
    check ((line_kind = 'CHARGE') = (charge_treatment is not null))
);
create index if not exists purchase_invoice_line_order on core.purchase_invoice_line (tenant_id, company_id, purchase_order_id, purchase_order_line_no) where purchase_order_id is not null;
create index if not exists purchase_invoice_line_receipt on core.purchase_invoice_line (tenant_id, company_id, receipt_id, receipt_line_no) where receipt_id is not null;

do $$
declare t text;
begin
    foreach t in array array['purchase_invoice', 'purchase_invoice_line'] loop
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

-- the match clears GR/IR items in the transaction of the invoice: the clearing tables, the S columns of an open item and the CLEARING head (as 0040 gave the stock role)
grant insert on core.clearing, core.clearing_item to cmd_post;
grant update (clearing_status, cleared_amount_tc, cleared_amount_lc, cleared_amount_gc, cleared_amount_rc, last_clearing_id) on core.journal_line_open_item to cmd_post;

drop policy if exists chain_head_post on core.chain_head;
create policy chain_head_post on core.chain_head as restrictive for update to cmd_post
    using (chain_type in ('LEDGER', 'VAT_RETURN', 'CLEARING')) with check (chain_type in ('LEDGER', 'VAT_RETURN', 'CLEARING'));

-- the guard of the open item row (copy of 0040) lets one more role change the S columns
create or replace function core.guard_journal_line_open_item() returns trigger language plpgsql as $$
declare
    v_year int; v_period int;
    f text;
    l2 constant text[] := array['baseline_date', 'payment_terms_id', 'due_date', 'discount_due_date', 'discount_amount_tc',
                                'payment_block', 'dunning_block', 'payment_reference', 'bank_account_id'];
    s  constant text[] := array['clearing_status', 'cleared_amount_tc', 'cleared_amount_lc', 'cleared_amount_gc', 'cleared_amount_rc', 'last_clearing_id'];
begin
    if (to_jsonb(new) - l2 - s) is distinct from (to_jsonb(old) - l2 - s) then
        raise exception 'journal_line_open_item: an L1 column cannot change' using errcode = '23000';
    end if;
    if (to_jsonb(new) - l2) is distinct from (to_jsonb(old) - l2)
       and not (pg_has_role(current_user, 'cmd_clearing', 'member') or pg_has_role(current_user, 'cmd_stock', 'member') or pg_has_role(current_user, 'cmd_post', 'member')) then
        raise exception 'journal_line_open_item: the S columns change only by the clearing role' using errcode = '23000';
    end if;
    -- the due date of an open item on a control account stays mandatory (02, 5A, 06, 5.4)
    if new.due_date is null and old.due_date is not null and exists (
        select 1 from core.journal_line l join core.gl_account a on a.tenant_id = l.tenant_id and a.company_id = l.company_id and a.account_id = l.account_id
         where l.tenant_id = new.tenant_id and l.company_id = new.company_id and l.document_id = new.document_id and l.line_no = new.line_no
           and a.open_item_mode = 'CONTROL') then
        raise exception 'ACCOUNT_RULE.DUE_DATE_REQUIRED: the due date of an open item on a control account cannot be cleared' using errcode = '23000';
    end if;
    select fiscal_year, fiscal_period into v_year, v_period from core.journal_line
     where tenant_id = new.tenant_id and company_id = new.company_id and document_id = new.document_id and line_no = new.line_no;
    foreach f in array l2 loop
        if (to_jsonb(new) ->> f) is distinct from (to_jsonb(old) ->> f) then
            perform core.require_l2_log(new.tenant_id, new.company_id, new.document_id, new.line_no, f,
                                        to_jsonb(old) ->> f, to_jsonb(new) ->> f, v_year, v_period, 'LINE_OPEN_ITEM');
        end if;
    end loop;
    return new;
end $$;

insert into platform.schema_migration (version, description)
values ('0046', 'purchase invoices: the link records of a matched supplier invoice, the posting role clears GR/IR items')
on conflict (version) do nothing;
