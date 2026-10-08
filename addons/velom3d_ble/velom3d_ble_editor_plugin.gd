@tool
extends EditorPlugin

# Ajoute le plugin Android Velom3dBle (AAR construit depuis android-plugin/) aux exports Android.
# Côté jeu, il est utilisé par Sensors/Backends/NativeBleBackend.cs.

var _export_plugin: AndroidExportPlugin


func _enter_tree() -> void:
	_export_plugin = AndroidExportPlugin.new()
	add_export_plugin(_export_plugin)


func _exit_tree() -> void:
	remove_export_plugin(_export_plugin)
	_export_plugin = null


class AndroidExportPlugin extends EditorExportPlugin:
	const PLUGIN_NAME := "Velom3dBle"
	const BIN_DIR := "res://addons/velom3d_ble/bin/"

	func _supports_platform(platform: EditorExportPlatform) -> bool:
		return platform is EditorExportPlatformAndroid

	func _get_android_libraries(platform: EditorExportPlatform, debug: bool) -> PackedStringArray:
		var file := "velom3d_ble-%s.aar" % ("debug" if debug else "release")
		if not FileAccess.file_exists(BIN_DIR + file):
			push_warning("Velom3d BLE : %s introuvable. Construisez-le avec « cd android-plugin && ./gradlew assemble » ; l'export continue sans Bluetooth natif." % file)
			return PackedStringArray()
		return PackedStringArray(["velom3d_ble/bin/" + file])

	func _get_name() -> String:
		return PLUGIN_NAME
