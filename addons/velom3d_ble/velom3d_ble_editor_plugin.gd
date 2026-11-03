@tool
extends EditorPlugin

# Bluetooth dans les exports :
# - Android : ajoute le plugin Velom3dBle (AAR construit depuis android-plugin/).
#   Côté jeu, il est utilisé par Sensors/Backends/NativeBleBackend.cs.
# - Windows : compile le programme d'aide Bluetooth (Desktop/BleHelper) et le copie à côté de
#   l'exécutable du jeu. Côté jeu : Sensors/Backends/WindowsBle.cs.

var _export_plugin: AndroidExportPlugin
var _windows_plugin: WindowsExportPlugin


func _enter_tree() -> void:
	_export_plugin = AndroidExportPlugin.new()
	add_export_plugin(_export_plugin)
	_windows_plugin = WindowsExportPlugin.new()
	add_export_plugin(_windows_plugin)


func _exit_tree() -> void:
	remove_export_plugin(_export_plugin)
	_export_plugin = null
	remove_export_plugin(_windows_plugin)
	_windows_plugin = null


class AndroidExportPlugin extends EditorExportPlugin:
	const PLUGIN_NAME := "Velom3dBle"
	const BIN_DIR := "res://addons/velom3d_ble/bin/"

	func _supports_platform(platform: EditorExportPlatform) -> bool:
		return platform is EditorExportPlatformAndroid

	func _get_android_libraries(platform: EditorExportPlatform, debug: bool) -> PackedStringArray:
		# Sans Gradle Build, Godot ignore les AAR : le Bluetooth manquerait sans autre signe.
		if not get_option("gradle_build/use_gradle_build"):
			push_error("Velom3d BLE : cochez « Use Gradle Build » dans le préréglage Android, sinon l'appli n'aura pas le Bluetooth.")
			return PackedStringArray()
		var file := "velom3d_ble-%s.aar" % ("debug" if debug else "release")
		if not FileAccess.file_exists(BIN_DIR + file):
			push_error("Velom3d BLE : %s introuvable dans %s. Copiez-y les AAR (artefact « velom3d_ble-aar » de la CI, ou « cd android-plugin && ./gradlew assemble »), sinon l'appli n'aura pas le Bluetooth." % [file, BIN_DIR])
			return PackedStringArray()
		return PackedStringArray(["velom3d_ble/bin/" + file])

	func _get_name() -> String:
		return PLUGIN_NAME


class WindowsExportPlugin extends EditorExportPlugin:
	const HELPER_PROJECT := "res://Desktop/BleHelper/Velom3d.BleHelper.csproj"

	func _supports_platform(platform: EditorExportPlatform) -> bool:
		return platform is EditorExportPlatformWindows

	func _get_name() -> String:
		return "Velom3dBleHelper"

	func _export_begin(features: PackedStringArray, _is_debug: bool, path: String, _flags: int) -> void:
		if not features.has("windows"):
			return
		var out_path := path
		if out_path.is_relative_path():
			out_path = ProjectSettings.globalize_path("res://" + out_path)
		elif out_path.begins_with("res://"):
			out_path = ProjectSettings.globalize_path(out_path)
		var out_dir := out_path.get_base_dir()
		DirAccess.make_dir_recursive_absolute(out_dir)
		var project := ProjectSettings.globalize_path(HELPER_PROJECT)
		var output := []
		print("Velom3d : compilation du programme d'aide Bluetooth de Windows dans %s…" % out_dir)
		var code := OS.execute("dotnet", ["publish", project, "-c", "Release", "-p:DebugType=none", "-o", out_dir], output, true)
		if code != 0:
			push_error("Velom3d : le programme d'aide Bluetooth de Windows n'a pas pu être compilé (dotnet publish, code %d). Sans lui, ce PC n'utilisera que le relais par téléphone.\n%s" % [code, "\n".join(output)])
