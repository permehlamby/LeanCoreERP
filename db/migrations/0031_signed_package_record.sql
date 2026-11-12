-- 0031 The record of a package apply keeps what proves where the package came from (20, 11, ADR-217): the SHA-256 of the signed archive, the id of the key that
-- signed it and the SHA-256 of the signing manifest. Null for an apply from a directory, which only a non-PRODUCTION tenant accepts.

alter table core.applied_config_package add column if not exists archive_sha256 varchar(64);
alter table core.applied_config_package add column if not exists signer_key_id varchar(100);
alter table core.applied_config_package add column if not exists signing_manifest_sha256 varchar(64);

insert into platform.schema_migration (version, description)
values ('0031', 'the archive digest, the signer key and the signing manifest digest in the record of a package apply')
on conflict (version) do nothing;
