CREATE TABLE `global_status` (
	`id` integer PRIMARY KEY DEFAULT 1 NOT NULL,
	`steam_path` text,
	`steam_exe_path` text,
	`steam_pid` integer,
	`steam_client_dll_path` text,
	`steam_client_dll_64_path` text,
	`active_user_steam_id` blob,
	`running_app_id` integer,
	`refresh_time` integer NOT NULL,
	CONSTRAINT "global_status_check_id" CHECK("global_status"."id" == 1)
);
--> statement-breakpoint
CREATE UNIQUE INDEX `global_status_id_unique` ON `global_status` (`id`);--> statement-breakpoint
CREATE TABLE `steam_app` (
	`id` integer PRIMARY KEY AUTOINCREMENT NOT NULL,
	`app_id` integer NOT NULL,
	`name` text,
	`name_localized` text DEFAULT '{}' NOT NULL,
	`installed` integer NOT NULL,
	`install_dir` text,
	`install_dir_path` text,
	`app_on_disk` blob,
	`app_on_disk_real` blob,
	`is_running` integer DEFAULT false NOT NULL,
	`type` text,
	`developer` text,
	`publisher` text,
	`steam_release_date` integer,
	`is_free_app` integer,
	`refresh_time` integer NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `steam_app_id_unique` ON `steam_app` (`id`);--> statement-breakpoint
CREATE UNIQUE INDEX `steam_app_app_id_unique` ON `steam_app` (`app_id`);--> statement-breakpoint
CREATE INDEX `steam_app_name_idx` ON `steam_app` (`name`);--> statement-breakpoint
CREATE INDEX `steam_app_installed_idx` ON `steam_app` (`installed`);--> statement-breakpoint
CREATE TABLE `steam_user` (
	`id` integer PRIMARY KEY AUTOINCREMENT NOT NULL,
	`steam_id` blob NOT NULL,
	`account_id` integer NOT NULL,
	`account_name` text NOT NULL,
	`persona_name` text,
	`remember_password` integer,
	`refresh_time` integer NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `steam_user_id_unique` ON `steam_user` (`id`);--> statement-breakpoint
CREATE UNIQUE INDEX `steam_user_steam_id_unique` ON `steam_user` (`steam_id`);--> statement-breakpoint
CREATE UNIQUE INDEX `steam_user_account_id_unique` ON `steam_user` (`account_id`);--> statement-breakpoint
CREATE INDEX `steam_user_account_name_idx` ON `steam_user` (`account_name`);--> statement-breakpoint
CREATE TABLE `use_app_record` (
	`id` integer PRIMARY KEY AUTOINCREMENT NOT NULL,
	`app_id` integer NOT NULL,
	`steam_id` blob NOT NULL,
	`start_time` integer NOT NULL,
	`end_time` integer,
	`duration` integer
);
--> statement-breakpoint
CREATE UNIQUE INDEX `use_app_record_id_unique` ON `use_app_record` (`id`);--> statement-breakpoint
CREATE INDEX `use_app_record_app_id_idx` ON `use_app_record` (`app_id`);--> statement-breakpoint
CREATE INDEX `use_app_record_steam_id_idx` ON `use_app_record` (`steam_id`);--> statement-breakpoint
CREATE INDEX `use_app_record_start_time_idx` ON `use_app_record` (`start_time`);--> statement-breakpoint
CREATE INDEX `use_app_record_steam_id_app_id_idx` ON `use_app_record` (`steam_id`,`app_id`);