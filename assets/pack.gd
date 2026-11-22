extends SceneTree

func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 3:
		quit(1)
		return
	var coin := _load_coin(args[2])
	if coin == null or coin.is_empty():
		push_error("Could not load the supplied coin image.")
		quit(1)
		return
	coin.convert(Image.FORMAT_RGBA8)
	var matte := coin.get_pixel(0, 0)
	if matte.a == 1.0:
		for y in coin.get_height():
			for x in coin.get_width():
				if coin.get_pixel(x, y) == matte:
					coin.set_pixel(x, y, Color.TRANSPARENT)
	var bounds := coin.get_used_rect()
	if not bounds.has_area():
		push_error("The supplied coin image has no visible pixels.")
		quit(1)
		return
	coin = coin.get_region(bounds)
	var error := ResourceSaver.save(ImageTexture.create_from_image(coin), args[1])
	if error != OK:
		push_error("Failed to generate coin texture: %s" % error)
		quit(1)
		return
	var packer := PCKPacker.new()
	error = packer.pck_start(args[0])
	if error == OK:
		error = packer.add_file("res://CrookedPenny/coin.res", args[1])
	for locale: String in ["eng", "zhs"]:
		if error == OK:
			var settings := "localization/%s/settings_ui.json" % locale
			error = packer.add_file("res://CrookedPenny/" + settings, ProjectSettings.globalize_path("res://" + settings))
	if error == OK:
		error = packer.flush()
	if error != OK:
		push_error("Failed to generate Crooked Penny resource pack: %s" % error)
	quit(0 if error == OK else 1)

func _load_coin(path: String) -> Image:
	if path.get_extension().to_lower() == "svg":
		var svg := Image.new()
		if svg.load_svg_from_buffer(FileAccess.get_file_as_bytes(path)) == OK:
			return svg
		return null
	return Image.load_from_file(path)
