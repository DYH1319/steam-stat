DROP INDEX `steam_user_account_name_idx`;--> statement-breakpoint
ALTER TABLE `steam_user` ADD `wants_offline_mode` integer;--> statement-breakpoint
ALTER TABLE `steam_user` ADD `skip_offline_mode_warning` integer;--> statement-breakpoint
ALTER TABLE `steam_user` ADD `allow_auto_login` integer;--> statement-breakpoint
ALTER TABLE `steam_user` ADD `most_recent` integer;--> statement-breakpoint
ALTER TABLE `steam_user` ADD `timestamp` integer;--> statement-breakpoint
ALTER TABLE `steam_user` ADD `avatar_full` blob;--> statement-breakpoint
ALTER TABLE `steam_user` ADD `avatar_medium` blob;--> statement-breakpoint
ALTER TABLE `steam_user` ADD `avatar_small` blob;--> statement-breakpoint
ALTER TABLE `steam_user` ADD `animated_avatar` blob;--> statement-breakpoint
ALTER TABLE `steam_user` ADD `avatar_frame` blob;--> statement-breakpoint
ALTER TABLE `steam_user` DROP COLUMN `avatar`;--> statement-breakpoint
ALTER TABLE `steam_user` DROP COLUMN `refresh_time`;--> statement-breakpoint
ALTER TABLE `global_status` ADD `steam_user_refresh_time` integer;
