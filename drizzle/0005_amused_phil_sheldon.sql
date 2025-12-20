PRAGMA foreign_keys=OFF;--> statement-breakpoint
CREATE TABLE `__new_steam_user` (
	`id` integer PRIMARY KEY AUTOINCREMENT NOT NULL,
	`steam_id` blob NOT NULL,
	`account_id` integer NOT NULL,
	`account_name` text NOT NULL,
	`persona_name` text,
	`remember_password` integer,
	`wants_offline_mode` integer,
	`skip_offline_mode_warning` integer,
	`allow_auto_login` integer,
	`most_recent` integer,
	`timestamp` integer,
	`avatar_full` text,
	`avatar_medium` text,
	`avatar_small` text,
	`animated_avatar` text,
	`avatar_frame` text,
	`level` integer,
	`level_class` text
);
--> statement-breakpoint
INSERT INTO `__new_steam_user`("id", "steam_id", "account_id", "account_name", "persona_name", "remember_password", "wants_offline_mode", "skip_offline_mode_warning", "allow_auto_login", "most_recent", "timestamp", "avatar_full", "avatar_medium", "avatar_small", "animated_avatar", "avatar_frame", "level", "level_class") SELECT "id", "steam_id", "account_id", "account_name", "persona_name", "remember_password", "wants_offline_mode", "skip_offline_mode_warning", "allow_auto_login", "most_recent", "timestamp", "avatar_full", "avatar_medium", "avatar_small", "animated_avatar", "avatar_frame", "level", "level_class" FROM `steam_user`;--> statement-breakpoint
DROP TABLE `steam_user`;--> statement-breakpoint
ALTER TABLE `__new_steam_user` RENAME TO `steam_user`;--> statement-breakpoint
PRAGMA foreign_keys=ON;--> statement-breakpoint
CREATE UNIQUE INDEX `steam_user_id_unique` ON `steam_user` (`id`);--> statement-breakpoint
CREATE UNIQUE INDEX `steam_user_steam_id_unique` ON `steam_user` (`steam_id`);--> statement-breakpoint
CREATE UNIQUE INDEX `steam_user_account_id_unique` ON `steam_user` (`account_id`);