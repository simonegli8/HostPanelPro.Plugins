# HostPanelPro.Plugins
The public repository for plugins of the HostPanelPro webhosting control panel.

In order to publish your own plugin, fork this repo on GitHub and put your plugin here in the Plugins folder, either as source code, or a 7z plugin package. Then you can create a pull request that we will review, and then add your plugin to the available plugins. You can also publish the plugin yourself on your fork, by adding the plugin to the Plugin folder and execute `publish.bat`. This will create a plugin feed source website in the www folder. You then can publish this folder to the web, and then add the url of this website to your plugin feeds.

If your plugin needs to extend the Portal's UI, you can add ascx pages to the Pages folder in the Portal project of your plugin. Those pages will be treated as if they'd reside in the HostPanelPro.Portal.Modules project.