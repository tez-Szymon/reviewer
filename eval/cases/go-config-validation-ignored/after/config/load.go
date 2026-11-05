package config

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
)

type Config struct {
	Port    int    `json:"port"`
	DataDir string `json:"dataDir"`
}

// Load reads the config at path, fills in defaults and checks it.
func Load(path string) (*Config, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var cfg Config
	if err := json.Unmarshal(data, &cfg); err != nil {
		return nil, err
	}
	if cfg.DataDir == "" {
		home, err := os.UserHomeDir()
		if err != nil {
			return nil, err
		}
		cfg.DataDir = filepath.Join(home, ".app")
	}
	if cfg.Port == 0 {
		cfg.Port = 8080
	}
	err = validate(&cfg)
	return &cfg, nil
}

func validate(cfg *Config) error {
	if cfg.Port < 1 || cfg.Port > 65535 {
		return fmt.Errorf("port %d is out of range", cfg.Port)
	}
	if !filepath.IsAbs(cfg.DataDir) {
		return fmt.Errorf("dataDir %q must be an absolute path", cfg.DataDir)
	}
	return nil
}
