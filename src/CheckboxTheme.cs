using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    void ConfigureCheckboxes()
    {
        // The system light checkbox template loses its tick on our dark surfaces.
        // Keep native ToggleButton input/keyboard behavior and draw every state explicitly.
        Resources[typeof(CheckBox)] = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
              <Setter Property="Foreground" Value="#DDE7F1"/>
              <Setter Property="Margin" Value="0,4"/>
              <Setter Property="Padding" Value="5,6"/>
              <Setter Property="MinHeight" Value="30"/>
              <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
              <Setter Property="VerticalContentAlignment" Value="Center"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="CheckBox">
                    <Border x:Name="HitArea" Background="Transparent" Padding="{TemplateBinding Padding}" CornerRadius="4">
                      <Grid>
                        <Grid.ColumnDefinitions><ColumnDefinition Width="20"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                        <Border x:Name="Box" Width="18" Height="18" Background="#182231" BorderBrush="#8499AF" BorderThickness="1.5" CornerRadius="3" VerticalAlignment="Center">
                          <Path x:Name="Tick" Data="M 3,8 L 7,12 L 14,4" Stroke="White" StrokeThickness="2" Visibility="Collapsed" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                        </Border>
                        <ContentPresenter Grid.Column="1" Margin="7,0,0,0" RecognizesAccessKey="True" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                      </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsChecked" Value="True"><Setter TargetName="Tick" Property="Visibility" Value="Visible"/><Setter TargetName="Box" Property="Background" Value="#267E8D"/><Setter TargetName="Box" Property="BorderBrush" Value="#7AE2DD"/></Trigger>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="HitArea" Property="Background" Value="#263849"/><Setter TargetName="Box" Property="BorderBrush" Value="#A4F0EB"/></Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="HitArea" Property="Background" Value="#30465A"/><Setter TargetName="Box" Property="BorderBrush" Value="White"/></Trigger>
                      <Trigger Property="IsEnabled" Value="False"><Setter TargetName="HitArea" Property="Opacity" Value="0.4"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """);
    }
}
